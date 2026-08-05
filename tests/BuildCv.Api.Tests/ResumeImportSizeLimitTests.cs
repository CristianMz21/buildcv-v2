using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BuildCv.Api.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Api.Tests;

// The request-size ceiling on POST /resumes/import, in the two halves it actually has.
//
// IT IS NOT TESTED THROUGH ApiTestFactory, and that is the point. WebApplicationFactory runs on
// TestServer, which is not Kestrel and does not enforce IRequestSizeLimitMetadata at all — a 3 MiB body
// to the real endpoint answers 201 there. A test written against TestServer would therefore have to
// assert the WRONG answer, and would go green for a build with no limit at all.
//
// So the property is split: the endpoint DECLARES the ceiling (asserted against the real application's
// endpoint metadata), and Kestrel ENFORCES that declaration (asserted against a real Kestrel host).
// Together they are the claim; neither alone is.
public sealed class ResumeImportSizeLimitTests
{
    private const int Port = 5237;

    [Fact]
    public void TheImportEndpointDeclaresTheCeiling()
    {
        using var factory = new ApiTestFactory();

        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/v1/resumes/import");

        endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize
            .Should().Be(ResumeEndpoints.ImportRequestSizeLimitBytes);
    }

    // WHY THIS TEST EXISTS AT ALL. An earlier revision measured a minimal-API endpoint carrying this
    // metadata, saw 200 for an oversized body, and concluded the framework enforces nothing — then added
    // a middleware to "fix" it. The measurement was wrong: that probe's handler took only HttpContext and
    // never read the request body, and Kestrel applies the limit while the body is READ. Nothing reads,
    // nothing refuses.
    //
    // The two endpoints below are that experiment, kept as a test. `/reads-body` binds a DTO, so the body
    // is read; `/ignores-body` is the original mistake. Same metadata, opposite answers.
    [Fact]
    public async Task KestrelEnforcesTheDeclaredCeiling_ButOnlyWhenTheBodyIsRead()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(Port));
        var app = builder.Build();

        app.MapPost("/reads-body", (Payload payload) => Results.Ok(payload.Value.Length))
            .WithMetadata(new RequestSizeLimitAttribute(1024));
        app.MapPost("/ignores-body", (HttpContext _) => Results.Ok("ok"))
            .WithMetadata(new RequestSizeLimitAttribute(1024));

        await app.StartAsync();

        try
        {
            using var client = new HttpClient();
            var underTheLimit = Body(100);
            var overTheLimit = Body(4000);

            (await Post(client, "/reads-body", underTheLimit)).StatusCode
                .Should().Be(HttpStatusCode.OK, "a body inside the ceiling is not refused");

            (await Post(client, "/reads-body", overTheLimit)).StatusCode
                .Should().Be(HttpStatusCode.RequestEntityTooLarge,
                    "Kestrel enforces IRequestSizeLimitMetadata with no middleware of ours involved");

            (await Post(client, "/reads-body", overTheLimit, chunked: true)).StatusCode
                .Should().Be(HttpStatusCode.RequestEntityTooLarge,
                    "the chunked case has no Content-Length and is still refused, while the body is read");

            (await Post(client, "/ignores-body", overTheLimit)).StatusCode
                .Should().Be(HttpStatusCode.OK,
                    "THIS is what the original faulty measurement measured: a handler that never reads "
                    + "its body is never refused for the size of one");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    // The 413 is the one response in this API that is not ProblemDetails-shaped, and it cannot be made
    // one: the connection is closed inside the server and no IExceptionHandler runs. Pinned so the
    // exception to the repo-wide convention is a stated fact rather than a surprise.
    [Fact]
    public async Task TheRefusalIsEmptyAndClosesTheConnection()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(Port + 1));
        builder.Services.AddProblemDetails();
        var app = builder.Build();

        app.UseExceptionHandler();
        app.MapPost("/reads-body", (Payload payload) => Results.Ok(payload.Value.Length))
            .WithMetadata(new RequestSizeLimitAttribute(1024));

        await app.StartAsync();

        try
        {
            using var client = new HttpClient();
            var response = await Post(client, "/reads-body", Body(4000), port: Port + 1);

            response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
            response.Content.Headers.ContentType.Should().BeNull();
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static string Body(int padding) => "{\"value\":\"" + new string('a', padding) + "\"}";

    private static async Task<HttpResponseMessage> Post(
        HttpClient client, string path, string body, bool chunked = false, int port = Port)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}{path}");

        if (chunked)
        {
            request.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            request.Headers.TransferEncodingChunked = true;
        }
        else
        {
            request.Content = new StringContent(body);
        }

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.SendAsync(request);
    }

    public sealed record Payload(string Value);
}
