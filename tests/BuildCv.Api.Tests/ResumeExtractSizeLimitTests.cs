using System.Net;
using System.Net.Http.Headers;
using BuildCv.Api.Common;
using BuildCv.Api.Endpoints;
using BuildCv.Application.Common.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Api.Tests;

// The request-size ceiling on POST /resumes/import/extract, split exactly as it is for
// /resumes/import: the endpoint DECLARES the ceiling (asserted against the real application's
// metadata) and Kestrel ENFORCES the declaration (asserted against a real Kestrel host, because
// TestServer does not enforce sizes at all — see ResumeImportSizeLimitTests for that argument).
//
// The multipart question this file answers by measurement, not documentation: TWO limits genuinely
// apply to a form body — the endpoint's IRequestSizeLimitMetadata, enforced by Kestrel on the raw
// request, and FormOptions.MultipartBodyLengthLimit, enforced by the form reader on the parts it
// buffers. For the extract endpoint the metadata fires: its 5 MiB sits far below the FormOptions
// default of 128 MiB, and the discriminating probe below (same oversized body, metadata removed,
// answer flips to 200) proves the 413 comes from the metadata rather than from anything else in the
// form path.
public sealed class ResumeExtractSizeLimitTests
{
    private const int Port = 5245;

    [Fact]
    public void TheExtractEndpointDeclaresTheCeiling()
    {
        using var factory = new ApiTestFactory();

        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/resumes/import/extract");

        endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize
            .Should().Be(IDocumentTextExtractor.MaxDocumentBytes);
    }

    [Fact]
    public async Task KestrelEnforcesTheDeclaredCeilingOnAMultipartBody()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(Port));
        var app = builder.Build();

        // The same shape as the real endpoint: IFormFile binding under IRequestSizeLimitMetadata,
        // FormOptions left at its defaults. `/unlimited` is the discriminator — identical binding,
        // no metadata — so the oversized answers below are attributable to the metadata alone.
        app.MapPost("/limited", (IFormFile file) => Results.Ok(file.Length))
            .WithMetadata(new RequestSizeLimitAttribute(1024))
            .DisableAntiforgery();
        app.MapPost("/unlimited", (IFormFile file) => Results.Ok(file.Length))
            .DisableAntiforgery();

        await app.StartAsync();

        try
        {
            using var client = new HttpClient();

            (await PostFile(client, "/limited", fileBytes: 100)).StatusCode
                .Should().Be(HttpStatusCode.OK, "a body inside the ceiling is not refused");

            (await PostFile(client, "/limited", fileBytes: 4000)).StatusCode
                .Should().Be(HttpStatusCode.RequestEntityTooLarge,
                    "Kestrel enforces IRequestSizeLimitMetadata on a multipart body like on any other");

            (await PostFile(client, "/limited", fileBytes: 4000, chunked: true)).StatusCode
                .Should().Be(HttpStatusCode.RequestEntityTooLarge,
                    "a chunked upload declares no Content-Length and is still refused while being read");

            (await PostFile(client, "/unlimited", fileBytes: 4000)).StatusCode
                .Should().Be(HttpStatusCode.OK,
                    "with the metadata removed the same body sails through, so the 413s above are the "
                    + "metadata firing and not FormOptions or any other part of the form path");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    // The OTHER limit, made to fire on purpose so its behavior is a measured fact rather than a
    // guess: FormOptions.MultipartBodyLengthLimit genuinely bounds the form body, but it fires inside
    // the FORM READER during binding, not inside Kestrel — so it is not a 413. Measured: minimal-API
    // binding surfaces it as a BadHttpRequestException, which under this app's error configuration
    // (ThrowOnBadRequest everywhere + MalformedRequestExceptionHandler, both carried by this host)
    // answers 400 ProblemDetails. Today it never fires first on the extract endpoint — 128 MiB
    // default against the endpoint's 5 MiB metadata — which is why the client-visible refusal there
    // is the 413 and why the metadata ceiling must stay below this limit.
    [Fact]
    public async Task TheFormOptionsLimit_WhenItIsTheSmallerOne_IsA400NotA413()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(Port + 1));
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 1024);
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<MalformedRequestExceptionHandler>();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        var app = builder.Build();

        app.UseExceptionHandler();
        app.MapPost("/upload", (IFormFile file) => Results.Ok(file.Length))
            .DisableAntiforgery();

        await app.StartAsync();

        try
        {
            using var client = new HttpClient();
            var response = await PostFile(client, "/upload", fileBytes: 4000, port: Port + 1);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "the form reader refuses the body during binding, inside the request — Kestrel never "
                + "sees a size violation, so there is no 413");
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json",
                "binding failures are the class of refusal this app CAN shape, unlike the 413");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<HttpResponseMessage> PostFile(
        HttpClient client, string path, int fileBytes, bool chunked = false, int port = Port)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}{path}");

        var filePart = chunked
            ? (HttpContent)new StreamContent(new MemoryStream(new byte[fileBytes]))
            : new ByteArrayContent(new byte[fileBytes]);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        var content = new MultipartFormDataContent { { filePart, "file", "cv.txt" } };
        request.Content = content;
        if (chunked)
        {
            request.Content.Headers.ContentLength = null;
            request.Headers.TransferEncodingChunked = true;
        }

        return await client.SendAsync(request);
    }
}
