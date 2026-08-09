using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BuildCv.Api.Tests;

// The published OpenAPI document, read the way a client generator reads it.
//
// It is checked here because it is the only artifact in this repository that NOTHING else validates:
// V1ContractShapeTests walks the real response bodies, so a document that disagrees with them — or
// says nothing at all — passes every other test in the suite while every generated client is wrong.
public sealed class OpenApiDocumentTests
{
    // The union .NET emits for a double, so that "NaN" and "Infinity" can arrive as strings. This API
    // never turns that on, and a client generated from the unnarrowed document types every score as
    // `number | string` — a branch each call site has to remember, and the one that is forgotten formats
    // a percentage bar from a string. FiniteNumberSchemaTransformer removes it; this is what keeps it
    // removed, including for the fields nobody has added yet.
    [Fact]
    public async Task NoSchemaOffersANumberAsAStringToo()
    {
        using var document = await FetchAsync();

        var offenders = new List<string>();
        Walk(document.RootElement.GetProperty("components").GetProperty("schemas"), "", offenders);

        offenders.Should().BeEmpty(
            "a floating-point field must be typed `number`, not `number | string`");
    }

    // The proof that .Produces<T> actually reaches the document. Scoring is the route a client meets
    // first, and it returns IResult like every other endpoint here — which is exactly why nothing can
    // infer its response and why it has to be stated.
    [Fact]
    public async Task TheScoringRouteDeclaresWhatItReturns()
    {
        using var document = await FetchAsync();

        var responses = document.RootElement
            .GetProperty("paths").GetProperty("/v1/scoring/score")
            .GetProperty("post").GetProperty("responses");

        responses.GetProperty("200")
            .GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/AnalysisResponse");

        // The error side is part of the contract too: every failure this API emits is
        // ProblemDetails-shaped, and a document that only described the happy path would let a client
        // be generated with no error type at all.
        responses.GetProperty("404")
            .GetProperty("content").GetProperty("application/problem+json")
            .GetProperty("schema").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/ProblemDetails");
    }

    private static void Walk(JsonElement element, string path, List<string> offenders)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array)
            {
                var members = type.EnumerateArray().Select(entry => entry.GetString()).ToArray();
                if (members.Contains("number") && members.Contains("string"))
                    offenders.Add(path);
            }

            foreach (var property in element.EnumerateObject())
                Walk(property.Value, $"{path}/{property.Name}", offenders);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var entry in element.EnumerateArray())
                Walk(entry, $"{path}[{index++}]", offenders);
        }
    }

    private static async Task<JsonDocument> FetchAsync()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // Anonymous on purpose: MapOpenApi is AllowAnonymous and Development-only, which is the state
        // ApiTestFactory forces.
        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the document must be served at all");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
