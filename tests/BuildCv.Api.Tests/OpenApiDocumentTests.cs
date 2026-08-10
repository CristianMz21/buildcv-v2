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
    public async Task NoSchemaOffersANumberOrAnIntegerAsAStringToo()
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

    // The coverage guard, and the reason this whole pass exists: an operation that declares no body is
    // indistinguishable, to a generator, from one that returns nothing. Every route here returns
    // IResult, so nothing infers it and nothing else in this suite notices when a new endpoint forgets.
    //
    // Success only. The error shapes are asserted per route where they differ; what must never regress
    // is that a caller can find out what a successful call gives back.
    [Fact]
    public async Task EveryV1OperationDeclaresWhatASuccessReturns()
    {
        using var document = await FetchAsync();

        var undeclared = new List<string>();
        var examined = 0;

        foreach (var route in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!route.Name.StartsWith("/v1/", StringComparison.Ordinal))
                continue;

            foreach (var operation in route.Value.EnumerateObject())
            {
                examined++;
                var responses = operation.Value.GetProperty("responses");

                var declaresSuccess = responses.EnumerateObject().Any(response =>
                    response.Name.StartsWith('2')
                    // 204 and 202 are complete answers on their own: each says there is no body, which is
                    // a statement rather than an omission. 202 joins 204 rather than being waved through
                    // as "another 2xx" — /v1/auth/password-reset answers it precisely BECAUSE it will not
                    // say what happened, since a body that varied would reveal whether the address is
                    // registered. A bodyless 202 is that refusal, stated.
                    //
                    // This does not weaken what the test was written for. The failure it catches is an
                    // operation with no `.Produces` AT ALL, which documents nothing because IResult is
                    // opaque to the generator; declaring a bare 202 is still an author saying so.
                    && (response.Name is "204" or "202" || response.Value.TryGetProperty("content", out _)));

                if (!declaresSuccess)
                    undeclared.Add($"{operation.Name.ToUpperInvariant()} {route.Name}");
            }
        }

        // WITHOUT THIS THE TEST CANNOT FAIL. A route prefix that stops matching — a version bump, a
        // group rename — empties the loop, and an empty `undeclared` reads exactly like full coverage.
        // The floor is well under the real count so it pins the mechanism rather than the inventory.
        examined.Should().BeGreaterThan(40, "the walk must actually have reached the v1 operations");

        undeclared.Should().BeEmpty(
            "every v1 operation must state its success shape — nothing can infer it from IResult");
    }

    // 403 AND 404 TRAVEL TOGETHER, and only those two. They are the same question asked twice about an
    // owned resource: 403 says it exists and is not yours, 404 says it is not there — and a route that
    // can answer either can answer the other, because the caller naming an id cannot know which case
    // they are in. ProducesResultProblems() declares them together for exactly that reason.
    //
    // 400 IS NOT PART OF THE PAIR, and getting that wrong twice is what shaped this test:
    //   - Demanding all three from anything declaring 400 failed POST /v1/resumes/import, correctly.
    //     That route creates a resume the caller owns by construction, so no other owner can forbid it
    //     and no existing entity can be missing; its failures are field errors that never reach
    //     ToHttpResult. A 400-only operation is a complete statement, not an omission.
    //   - Demanding 400 alongside 403/404 then failed GET /v1/scoring/{analysisId}, correctly AT THE
    //     TIME. Its handler makes no Domain-factory calls, so nothing the handler returns can fall to
    //     ToHttpResult's else-arm, and declaring a 400 would have documented a response the route could
    //     not give — the same defect as omitting one it can.
    //     THAT ROUTE DECLARES 400 NOW, and the reason is worth keeping: the 400 never came from the
    //     handler and still does not. `new AnalysisId(analysisId)` runs first, and the empty guid the
    //     `:guid` constraint matches used to escape as a bare ArgumentException and answer 500 with a C#
    //     parameter name in the body. It is EmptyIdentifierException — a DomainException — since then,
    //     so the refusal is an ordinary 400. Reachability is decided per route by what the whole lambda
    //     can produce, never by the handler alone; ByIdRouteTests pins the answer end to end.
    //
    // This exists because POST /v1/scoring/score did not. It was hand-annotated before that helper
    // existed, was not swept when the helper landed, and shipped declaring {200, 400, 404} while the
    // comment directly above the list named 403 as one of the codes it answers. The handler returns
    // "Forbidden." on two reachable paths and a passing test already proved the 403 — the document was
    // the only thing that disagreed, and a client generated from it had no typed case for an ordinary
    // refusal. A .Produces that lies is worse than one that says nothing.
    [Fact]
    public async Task AnOperationThatCanForbidCanAlsoNotFind()
    {
        using var document = await FetchAsync();

        var incomplete = new List<string>();
        var examined = 0;

        foreach (var route in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!route.Name.StartsWith("/v1/", StringComparison.Ordinal))
                continue;

            foreach (var operation in route.Value.EnumerateObject())
            {
                var responses = operation.Value.GetProperty("responses");
                var declared = responses.EnumerateObject().Select(response => response.Name).ToHashSet();

                var pair = new[] { "403", "404" };
                if (!pair.Any(declared.Contains))
                    continue;

                examined++;
                var missing = pair.Where(code => !declared.Contains(code)).ToArray();
                if (missing.Length > 0)
                {
                    incomplete.Add(
                        $"{operation.Name.ToUpperInvariant()} {route.Name} declares "
                        + $"[{string.Join(", ", declared.Where(pair.Contains))}] "
                        + $"but not [{string.Join(", ", missing)}]");
                }
            }
        }

        // Same guard as above, and for the same reason: an empty list of offenders means nothing unless
        // the walk reached operations that carry refusals at all.
        examined.Should().BeGreaterThan(20,
            "the walk must actually have reached the operations that declare refusals");

        incomplete.Should().BeEmpty(
            "403 and 404 are one question about an owned resource asked twice, so declaring one and "
            + "omitting the other tells a generated client an ordinary refusal cannot happen");
    }

    private static void Walk(JsonElement element, string path, List<string> offenders)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array)
            {
                var members = type.EnumerateArray().Select(entry => entry.GetString()).ToArray();

                // BOTH numeric kinds. The first version of this test checked only "number" and passed
                // while every `int32` in the document — including the entry `id` that DELETE
                // /v1/resumes/{id}/{section}/{itemId} takes — was still typed `number | string`.
                if (members.Contains("string")
                    && (members.Contains("number") || members.Contains("integer")))
                {
                    offenders.Add(path);
                }
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
