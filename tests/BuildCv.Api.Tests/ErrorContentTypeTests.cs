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
