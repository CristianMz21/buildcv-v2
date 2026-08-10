using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Identity;
using BuildCv.Domain.Common.ValueObjects;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Api.Tests;

// Forgetting a password, over HTTP. Until this shipped, a forgotten password was a permanently lost CV:
// /auth/change-password requires the current one, so there was no path back for the user OR for support.
//
// Two properties carry the weight here, and neither is visible in a happy-path assertion: the endpoint
// must answer identically for a registered and an unregistered address, and the link must stop working
// after it is used. Both are tested against the responses a stranger can actually see.
public sealed class PasswordResetTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string NewPassword = "An0ther!Password#2026";

    // Records what would have been sent, so the whole flow is testable with no provider chosen. It is a
    // fake in tests only -- the sender that SHIPS refuses, because a real deployment answering "check your
    // inbox" with no mailer is worse than one that says the feature is off.
    private sealed class RecordingEmailSender : IEmailSender
    {
        private readonly List<EmailMessage> _sent = [];

        public IReadOnlyList<EmailMessage> Sent => _sent.AsReadOnly();

        public bool IsConfigured => true;

        public Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            _sent.Add(message);
            return Task.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task Reset_LetsSomebodyWhoForgotTheirPasswordSignInWithANewOne()
    {
        var mailer = new RecordingEmailSender();
        using var factory = FactoryWith(mailer);
        using var client = BearerClient(factory);
        await client.RegisterAsync(TestHelpers.CandidateEmail);

        (await RequestResetAsync(client, TestHelpers.CandidateEmail)).StatusCode
            .Should().Be(HttpStatusCode.Accepted);

        var token = TokenFrom(mailer);
        (await ConfirmAsync(client, token, NewPassword)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The real proof: the new password works and the old one does not.
        (await LoginAsync(client, TestHelpers.CandidateEmail, NewPassword)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await LoginAsync(client, TestHelpers.CandidateEmail, TestHelpers.Password)).StatusCode
            .Should().NotBe(HttpStatusCode.OK, "the password it replaced must stop working");
    }

    // Single use, with nothing stored to make it so: the token is signed over the password hash, and
    // succeeding changes the hash.
    [Fact]
    public async Task Reset_UsingTheSameLinkTwice_FailsTheSecondTime()
    {
        var mailer = new RecordingEmailSender();
        using var factory = FactoryWith(mailer);
        using var client = BearerClient(factory);
        await client.RegisterAsync(TestHelpers.CandidateEmail);
        await RequestResetAsync(client, TestHelpers.CandidateEmail);
        var token = TokenFrom(mailer);

        (await ConfirmAsync(client, token, NewPassword)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await ConfirmAsync(client, token, "AThird!Password#2026");
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the link is spent by succeeding");

        // And the second attempt changed nothing.
        (await LoginAsync(client, TestHelpers.CandidateEmail, NewPassword)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // THE ENUMERATION TEST, and the reason the endpoint always answers 202. On this platform, learning
    // that an address is registered means learning that person is looking for work.
    [Fact]
    public async Task Request_AnswersIdenticallyForARegisteredAndAnUnregisteredAddress()
    {
        var mailer = new RecordingEmailSender();
        using var factory = FactoryWith(mailer);
        using var client = BearerClient(factory);
        await client.RegisterAsync(TestHelpers.CandidateEmail);

        var known = await RequestResetAsync(client, TestHelpers.CandidateEmail);
        var unknown = await RequestResetAsync(client, "nobody-here@example.com");

        known.StatusCode.Should().Be(unknown.StatusCode);
        (await known.Content.ReadAsStringAsync()).Should().Be(await unknown.Content.ReadAsStringAsync());

        // Asserted so the equality above is not two identical failures. One mail was sent, and only one.
        mailer.Sent.Should().ContainSingle("the registered address gets a link and the other does not");
        mailer.Sent[0].To.Should().Be(TestHelpers.CandidateEmail);
    }

    // A wrong token, an expired one and a spent one all answer the same way. Telling them apart would say
    // whether the account exists, which is what the 202 above exists to hide.
    [Fact]
    public async Task Confirm_WithAForgedToken_AnswersTheSameAsASpentOne()
    {
        var mailer = new RecordingEmailSender();
        using var factory = FactoryWith(mailer);
        using var client = BearerClient(factory);
        await client.RegisterAsync(TestHelpers.CandidateEmail);
        await RequestResetAsync(client, TestHelpers.CandidateEmail);
        var token = TokenFrom(mailer);
        await ConfirmAsync(client, token, NewPassword);

        var spent = await ConfirmAsync(client, token, "AThird!Password#2026");
        var forged = await ConfirmAsync(client, "bm90LWEtcmVhbC10b2tlbg.c2lnbmF0dXJl", "AThird!Password#2026");

        forged.StatusCode.Should().Be(spent.StatusCode);
        // `detail`, not the whole body: traceId is per request by design, so comparing raw bodies would
        // fail on a difference that carries no information about any account.
        DetailOf(await forged.Content.ReadAsStringAsync()).Should().Be(
            DetailOf(await spent.Content.ReadAsStringAsync()),
            "a forged token and a spent one must be indistinguishable");
    }

    // The password policy runs BEFORE the token is examined, so a user who mistypes their new password
    // does not burn the one link they were sent.
    [Fact]
    public async Task Confirm_WithATooShortPassword_DoesNotSpendTheLink()
    {
        var mailer = new RecordingEmailSender();
        using var factory = FactoryWith(mailer);
        using var client = BearerClient(factory);
        await client.RegisterAsync(TestHelpers.CandidateEmail);
        await RequestResetAsync(client, TestHelpers.CandidateEmail);
        var token = TokenFrom(mailer);

        (await ConfirmAsync(client, token, "short")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ConfirmAsync(client, token, NewPassword)).StatusCode.Should().Be(
            HttpStatusCode.NoContent, "a rejected new password must not consume the link");
    }

    // With no provider configured -- which is what ships -- the endpoint says so instead of telling
    // somebody to watch an inbox that will never receive anything. The same answer for every address, so
    // it is not the oracle the 202 avoids.
    [Fact]
    public async Task Request_WithNoMailProviderConfigured_Answers503ForEveryAddress()
    {
        using var factory = new ApiTestFactory();
        using var client = BearerClient(factory);
        await client.RegisterAsync(TestHelpers.CandidateEmail);

        var known = await RequestResetAsync(client, TestHelpers.CandidateEmail);
        var unknown = await RequestResetAsync(client, "nobody-here@example.com");

        known.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        unknown.StatusCode.Should().Be(
            HttpStatusCode.ServiceUnavailable,
            "answering 503 only for registered addresses would invert the whole precaution");
    }

    private static string DetailOf(string problemJson) =>
        JsonDocument.Parse(problemJson).RootElement.GetProperty("detail").GetString()!;

    private static ApiTestFactory FactoryWith(IEmailSender mailer) =>
        new(configureServices: services => services.AddSingleton(mailer));

    private static HttpClient BearerClient(ApiTestFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    // Pulled out of the message body rather than out of the protector, so the test reads what a user
    // reads. A link the mail does not actually contain would fail here.
    private static string TokenFrom(RecordingEmailSender mailer)
    {
        mailer.Sent.Should().NotBeEmpty("no mail was sent, so there is no link to click");

        var body = mailer.Sent[^1].Body;
        var marker = $"?token=";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the mail must carry a link with a token in it");

        var rest = body[(start + marker.Length)..];
        var end = rest.IndexOfAny(['\n', '\r', ' ']);
        return Uri.UnescapeDataString(end < 0 ? rest : rest[..end]);
    }

    private static async Task<HttpResponseMessage> RequestResetAsync(HttpClient client, string email) =>
        await client.PostAsJsonAsync("/v1/auth/password-reset", new { email }, Web);

    private static async Task<HttpResponseMessage> ConfirmAsync(
        HttpClient client, string token, string newPassword) =>
        await client.PostAsJsonAsync("/v1/auth/password-reset/confirm", new { token, newPassword }, Web);

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string email, string password) =>
        await client.PostAsJsonAsync("/v1/auth/login", new { email, password }, Web);
}
