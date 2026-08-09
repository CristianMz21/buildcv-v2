using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BuildCv.Api.Tests;

// THE HALF OF "EVERY ERROR IS PROBLEMDETAILS" NOBODY WAS ASSERTING.
//
// The claim was true of the BODIES and false of the CONTENT TYPES, and it stayed false because every
// test in this repository reads bodies. The cause is one overload: WriteAsJsonAsync(value) sets
// Response.ContentType to "application/json" itself, so the familiar
//
//     context.Response.ContentType = "application/problem+json";
//     await context.Response.WriteAsJsonAsync(new ProblemDetails { ... });
//
// silently loses the line above it. CsrfGuardMiddleware and MalformedRequestExceptionHandler pass an
// explicit content type and were always right; the other four call sites in ApiExceptionHandlers and
// the JWT challenge were not swept when the first of those was fixed.
//
// It matters because dispatching on the content type is the documented way to consume RFC 7807. A
// client that does so cannot tell an error body from a success body, and a client that instead
// dispatches on the status code is doing the thing ProblemDetails exists to make unnecessary.
//
// Every case here checks the content type AND identifies the writer by something only that writer
// produces. A bare "400 with problem+json" would be satisfied by a binding failure that never reached
// the handler under test — two causes, one observable — so a title or a detail is asserted beside it.
public sealed class ErrorContentTypeTests
{
    // One lever for every handler. IDocumentTextExtractor is a port with no try/catch above it —
    // ExtractDocumentTextHandler documents that an exception from it is a bug rather than a 400 — so
    // whatever this throws travels the full IExceptionHandler chain, which is the code under test. Most
    // adapters are reached through a handler that catches DomainException and turns it into a Result,
    // which never reaches an exception handler at all, so this port is the shortest route to all four.
    [Theory]
    [InlineData(ThrowingDocumentTextExtractor.Domain, HttpStatusCode.BadRequest, "Bad Request")]
    [InlineData(ThrowingDocumentTextExtractor.Unauthorized, HttpStatusCode.Unauthorized, "Unauthorized")]
    [InlineData(ThrowingDocumentTextExtractor.DuplicateKey, HttpStatusCode.Conflict, "Conflict")]
    [InlineData(ThrowingDocumentTextExtractor.Concurrency, HttpStatusCode.Conflict, "Conflict")]
    [InlineData(ThrowingDocumentTextExtractor.ValueTooLong, HttpStatusCode.BadRequest, "Bad Request")]
    [InlineData(ThrowingDocumentTextExtractor.Unexpected, HttpStatusCode.InternalServerError, "Internal Server Error")]
    public async Task AnExceptionHandledResponse_IsTypedAsProblemJson(
        string failure, HttpStatusCode expectedStatus, string expectedTitle)
    {
        using var factory = new ApiTestFactory(configureServices: services =>
        {
            services.RemoveAll<IDocumentTextExtractor>();
            services.AddSingleton<IDocumentTextExtractor>(new ThrowingDocumentTextExtractor(failure));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await UploadAsync(client, token);

        response.StatusCode.Should().Be(expectedStatus);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json",
            "an error body a client cannot recognise by media type is not an RFC 7807 response");

        // Which handler answered, not merely which status. Without this a 400 produced by binding — or
        // a 500 produced by an escape rather than by GlobalExceptionHandler — would satisfy the line
        // above while proving nothing about the handler this case names.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().Be(expectedTitle);
        body.RootElement.GetProperty("status").GetInt32().Should().Be((int)expectedStatus);
    }

    // The JWT challenge, which is a different writer again: JwtBearerEvents.OnChallenge assigned
    // Response.ContentType and then called the overwriting overload, so the assignment never survived.
    // It is the error response an idle client meets first — every request with no usable credential
    // reaches it — and it is emitted before any endpoint runs, so nothing downstream can correct it.
    [Fact]
    public async Task AChallengedRequest_IsTypedAsProblemJson()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync("/v1/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        // The body was already a valid RFC 7807 document — type, title and status are all optional
        // members and a document carrying only those three is conformant. Pinned so the fix to the
        // media type is not read as licence to reshape what clients already parse.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("type").GetString().Should().Be("about:blank");
        body.RootElement.GetProperty("title").GetString().Should().Be("Unauthorized");
        body.RootElement.GetProperty("status").GetInt32().Should().Be(401);
    }

    // The two writers that were ALREADY correct, asserted here beside the five that were not, so the
    // sweep is stated in one place. Neither of these is a new behaviour; what is new is that removing
    // the explicit contentType argument from either now reds a test.
    [Fact]
    public async Task ACsrfRejection_IsTypedAsProblemJson()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateCookieClient();
        await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        // No X-XSRF-TOKEN header on a cookie-authenticated unsafe method, which is exactly what
        // CsrfGuardMiddleware refuses.
        var response = await client.PostAsJsonAsync("/v1/resumes", new
        {
            fullName = "Jane Candidate",
            email = TestHelpers.CandidateEmail,
            phoneNumber = (string?)null,
            location = (string?)null,
            summary = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString().Should().Be("CSRF validation failed.");
    }

    [Fact]
    public async Task AMalformedBody_IsTypedAsProblemJson()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes")
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json")
        }.WithBearer(token);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        // The detail is the framework's own binding message, which is what identifies
        // MalformedRequestExceptionHandler rather than DomainExceptionHandler — both title their 400
        // "Bad Request", so the title alone would not tell them apart.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString().Should().Contain("JSON");
    }

    // THE THIRD UNSHAPED REFUSAL, which was an omission rather than a platform limit.
    //
    // options.OnRejected set Retry-After, recorded a metric and returned, so every 429 from the
    // rate-limiting MIDDLEWARE — the global 100/min limiter and the `auth` and `logout` policies — came
    // back with Content-Length: 0 and no content type, while the three account-scoped limiters answered
    // real ProblemDetails from inside their endpoints. One class of refusal, two bodies.
    //
    // THE BODY IS ASSERTED, NOT THE STATUS. A 429 that is never emitted and a 429 with an empty body are
    // the same observable to a test that reads the status code alone, which is why the previous
    // rate-limit tests could not have caught this. The Retry-After header is asserted beside it because
    // writing a body starts the response, and anything that touched a header afterwards would be lost.
    [Fact]
    public async Task AThrottledRequest_IsTypedAsProblemJson()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        HttpResponseMessage? throttled = null;
        for (var i = 0; i < 6 && throttled is null; i++)
        {
            var attempt = await client.PostAsJsonAsync("/v1/auth/login",
                new { email = "nobody@example.com", password = "wrong-password" });
            if (attempt.StatusCode == HttpStatusCode.TooManyRequests)
                throttled = attempt;
        }

        throttled.Should().NotBeNull("the 5/min auth window must refuse the sixth attempt");

        // 429 AND a parsed body together is also the measurement that the middleware applies
        // RejectionStatusCode BEFORE invoking OnRejected. Were the order reversed, the write below would
        // have started the response and the later assignment would throw — this would read 500.
        throttled!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.Should().Contain(h => h.Key == "Retry-After",
            "the header is set before the body write and must survive it");

        // Asserted BEFORE the media type, because a body-less response has no ContentType at all and
        // dereferencing it would fail this test with a NullReferenceException — which says nothing about
        // what went wrong. This is the regression's own signature: Content-Length 0, no content type.
        (await throttled.Content.ReadAsStringAsync()).Should().NotBeEmpty(
            "a 429 with an empty body is the omission this test exists to catch");
        throttled.Content.Headers.ContentType.Should().NotBeNull(
            "an unshaped 429 carries no content type at all");
        throttled.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json",
            "a throttled caller must be able to recognise the refusal by media type like every other one");

        using var body = JsonDocument.Parse(await throttled.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().Be("Too Many Requests");
        body.RootElement.GetProperty("status").GetInt32().Should().Be(429);
        body.RootElement.GetProperty("detail").GetString().Should().Be("Too many requests.",
            "the middleware callback cannot name the limiter that refused, so the detail is generic");
    }

    // The two 429 WRITERS, compared against each other rather than each pinned alone. The middleware one
    // and the account-scoped one are written by different code — OnRejected talks to HttpResponse
    // directly, the endpoints call Results.Problem — so "both are ProblemDetails" is a claim about two
    // implementations agreeing, and only a comparison can hold them together.
    //
    // The DETAILS differ on purpose and are not compared: an endpoint limiter knows which account it
    // refused and says so, while OnRejectedContext carries only the HttpContext and the failed lease.
    [Fact]
    public async Task TheMiddlewareThrottleAndTheAccountThrottle_AnswerTheSameShape()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // Two of the five auth-window permits, spent here so the loop below can exhaust the rest.
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        HttpResponseMessage? accountThrottled = null;
        for (var i = 0; i < 6 && accountThrottled is null; i++)
        {
            using var request = ChangePasswordRequest(token, "wrong-password");
            var attempt = await client.SendAsync(request);
            if (attempt.StatusCode == HttpStatusCode.TooManyRequests)
                accountThrottled = attempt;
        }

        HttpResponseMessage? middlewareThrottled = null;
        for (var i = 0; i < 6 && middlewareThrottled is null; i++)
        {
            var attempt = await client.PostAsJsonAsync("/v1/auth/login",
                new { email = TestHelpers.CandidateEmail, password = "wrong-password" });
            if (attempt.StatusCode == HttpStatusCode.TooManyRequests)
                middlewareThrottled = attempt;
        }

        accountThrottled.Should().NotBeNull("PasswordChangeRateLimiter must refuse the sixth attempt");
        middlewareThrottled.Should().NotBeNull("the auth window must refuse once its permits are spent");

        foreach (var response in new[] { accountThrottled!, middlewareThrottled! })
        {
            response.Content.Headers.ContentType.Should().NotBeNull(
                "an unshaped 429 carries no content type at all");
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("title").GetString().Should().Be("Too Many Requests");
            body.RootElement.GetProperty("status").GetInt32().Should().Be(429);
            body.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    private static HttpRequestMessage ChangePasswordRequest(string accessToken, string currentPassword) =>
        new HttpRequestMessage(HttpMethod.Post, "/v1/auth/change-password")
        {
            Content = JsonContent.Create(new { currentPassword, newPassword = "An0ther!Password#2026" })
        }.WithBearer(accessToken);

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string token)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Curriculum vitae"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "cv.txt");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import/extract")
        {
            Content = content
        }.WithBearer(token);

        return await client.SendAsync(request);
    }

    private sealed class ThrowingDocumentTextExtractor(string failure) : IDocumentTextExtractor
    {
        public const string Domain = "domain";
        public const string Unauthorized = "unauthorized";
        public const string DuplicateKey = "duplicate-key";
        public const string Concurrency = "concurrency";
        public const string ValueTooLong = "value-too-long";
        public const string Unexpected = "unexpected";

        public Task<Result<DocumentExtraction>> ExtractAsync(
            Stream content, string? declaredContentType, CancellationToken cancellationToken = default) =>
            throw failure switch
            {
                Domain => new InvalidTechnologyException("Technology name is invalid."),
                Unauthorized => new UnauthorizedAccessException("No."),
                DuplicateKey => new DuplicateKeyException("Duplicate."),
                Concurrency => new ConcurrencyConflictException("Conflict."),
                ValueTooLong => new ValueTooLongException("Too long."),
                Unexpected => new InvalidOperationException("Extraction is unavailable."),
                _ => (Exception)new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown failure.")
            };
    }
}
