using System.Text.RegularExpressions;
using BuildCv.Api.Endpoints;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Resumes;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Api.Tests;

// docs/api-contract.md is the one artifact in this repository written for somebody OUTSIDE it, and the
// only one nothing else validates. Every number in it was correct when it was typed and is copied from a
// constant that can move without it -- which is the drift this project has spent its whole history
// chasing, except here the reader is a client developer who will act on the stale value rather than a
// maintainer who might notice.
//
// So the numbers are asserted against the constants themselves. Prose stays prose; the figures a client
// sizes its uploads and its retry budget against cannot silently stop being true.
public sealed class ApiContractDocumentTests
{
    private static readonly string DocumentPath = LocateDocument();

    [Fact]
    public void TheStatedBodyCeilingsAreTheOnesTheCodeEnforces()
    {
        var document = File.ReadAllText(DocumentPath);

        var expected = new (string Route, string Ceiling)[]
        {
            ("POST /v1/resumes/import", Human(ResumeEndpoints.ImportRequestSizeLimitBytes)),
            ("POST /v1/resumes/import/extract", Human(IDocumentTextExtractor.MaxDocumentBytes)),
            ("POST /v1/resumes/import/propose", Human(IDocumentTextExtractor.MaxDocumentBytes)),
            ("POST /v1/job-offers/import", Human(JobOfferEndpoints.ImportRequestSizeLimitBytes)),
        };

        foreach (var (route, ceiling) in expected)
        {
            // The row as the document writes it, so a changed constant fails on the row it belongs to
            // rather than somewhere vague.
            document.Should().MatchRegex(
                $@"\|\s*`{Regex.Escape(route)}`\s*\|\s*{Regex.Escape(ceiling)}\s*\|",
                $"the contract must state {route}'s real ceiling of {ceiling}");
        }
    }

    [Fact]
    public void TheStatedPerAccountWindowsAreTheOnesTheLimitersEnforce()
    {
        var document = File.ReadAllText(DocumentPath);

        // Only the account-scoped limiters are asserted. The middleware policies live in Program.cs as
        // inline literals with no constant to reference, so a test here would compare the document
        // against a number retyped in this file -- two copies agreeing with each other and neither with
        // the code, which is the tautology this repo already threw out of PageRequestTests.
        var expected = new[]
        {
            ResumeImportRateLimiter.PermitLimit,
            DocumentExtractionRateLimiter.PermitLimit,
            PasswordChangeRateLimiter.PermitLimit,
        };

        foreach (var permits in expected.Distinct())
        {
            // Spacing is the document's business, not this test's. The first version asserted the
            // literal "10/min" and failed against a table that writes "**10 / min**" — the document was
            // right and the assertion was too rigid, which is a test dictating prose rather than
            // checking a fact.
            document.Should().MatchRegex(
                $@"\*?\*?{permits}\s*/\s*min",
                $"a per-account limiter allows {permits} per minute and the contract must say so");
        }
    }

    // WITHOUT THIS THE TWO TESTS ABOVE CANNOT FAIL for the reason that matters. A moved or renamed
    // document makes File.ReadAllText throw, which reads as a broken test rather than a missing
    // contract -- and a document that quietly stopped existing is exactly the failure a client
    // developer would discover instead of us.
    [Fact]
    public void TheContractDocumentExistsWhereTheReadmeAndBriefsPointAt()
    {
        File.Exists(DocumentPath).Should().BeTrue(
            "docs/api-contract.md is the client-facing contract; moving it silently breaks every "
            + "reference to it");

        File.ReadAllText(DocumentPath).Length.Should().BeGreaterThan(2000,
            "an emptied document would satisfy every Contain assertion above by having nothing to "
            + "contradict");
    }

    // The numbers above were pinned and the ROUTE INVENTORY was not, which is exactly where the document
    // then drifted: it went on stating "the only PUT in the whole API is /v1/resumes/{id}/contact" after
    // two more shipped, and told clients that editing a resume item was impossible when the route for it
    // existed. A universal claim about the surface is the most useful sentence in a client contract and
    // the one most certain to rot, because nothing about adding an endpoint touches the prose.
    //
    // So the surface is asserted the same way the ceilings are: against the app's own endpoint table.
    // Scoped to /v1, which is what this document describes -- the health probes live outside it on
    // purpose, and the OpenAPI route is Development-only.
    [Fact]
    public void EveryVersionedRouteTheApiServes_IsNamedInTheDocument()
    {
        var document = File.ReadAllText(DocumentPath);

        using var factory = new ApiTestFactory();
        // Forces the host to build; the endpoint table does not exist before it does.
        using var client = factory.CreateClient();

        var missing = new List<string>();
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            if (!pattern.StartsWith("/v1", StringComparison.Ordinal))
                continue;

            var normalized = NormalizeRoute(pattern);
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                var line = $"{method} {normalized}";
                if (!document.Contains(line, StringComparison.Ordinal))
                    missing.Add(line);
            }
        }

        missing.Distinct().Should().BeEmpty(
            "docs/api-contract.md is written for a client developer who cannot read this repository, so a "
            + "route absent from it is a capability nobody outside can discover");
    }

    // Route constraints are an implementation detail of matching -- the document writes {id}, the code
    // writes {id:guid}. The ten per-section item routes are registered concretely and collapse back to the
    // one {section} family the document states, because ten identical rows would be noise rather than
    // contract.
    private static string NormalizeRoute(string pattern)
    {
        var withoutConstraints = Regex.Replace(pattern, @"\{(\w+)(?::[^}]+)?\}", "{$1}");

        var sections = Enum.GetNames<ResumeSection>()
            .Select(name => name.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var segments = withoutConstraints.Split('/')
            .Select(segment => sections.Contains(segment) ? "{section}" : segment);

        // A group's own root registers as "/v1/job-offers/" while the document -- and every client --
        // writes it without the trailing slash. Both forms route (measured: each answers 401, not 404),
        // so the slash is a registration artifact and normalizing it away is not hiding anything.
        return string.Join('/', segments).TrimEnd('/');
    }

    private static string Human(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024 * 1024)} MiB"
            : $"{bytes / 1024} KiB";

    // Walks up from the test assembly rather than assuming a working directory: `dotnet test` runs from
    // the project folder and CI from the repository root, and a path that only works in one of them
    // fails in the other for a reason nobody enjoys diagnosing.
    private static string LocateDocument()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "api-contract.md");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "docs/api-contract.md was not found above the test assembly. If it moved, these tests and "
            + "every reference to the contract need updating together.");
    }
}
