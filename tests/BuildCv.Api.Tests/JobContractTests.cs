using System.Net.Http.Json;
using System.Text.Json;
using BuildCv.Api.Contracts;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Api.Tests;

// The /jobs wire contract, asserted against the mapper directly so the two fields that matter can be
// pinned at all: nothing in the API can put an education level or a language requirement on a posting
// today (CreateJobRequest carries Title, CompanyName, CompanyId and Description, and there is no
// update endpoint), so a live request cannot distinguish a mapper that names those enums from one
// that does not. That absence is exactly why fixing the encoding is free right now and breaking
// later, and it is why the coverage has to come from here.
//
// The live half is asserted too, at the bottom: the shape the endpoint really returns today, so a
// mapper that is correct but not wired in cannot pass this file.
public class JobContractTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    // The whole point of the DTO. Both of these are 2 off the aggregate, and 2 is documented in Domain
    // as an append-only PERSISTENCE number; the moment a client reads it off the wire it is a public
    // API contract as well, and renumbering stops being a migration and becomes a breaking change.
    [Fact]
    public void Serialized_TheTwoEnumsThisChainAddedCarryNamesNotNumbers()
    {
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(JobPostingResponse.From(BuildFullPosting()), WebOptions));
        var root = json.RootElement;

        root.GetProperty("educationLevel").GetString().Should().Be("Bachelor");
        root.GetProperty("languageRequirements")[0].GetProperty("minimumLevel").GetString()
            .Should().Be("Professional");
    }

    // Not stated and HighSchool are different claims about a posting, and HighSchool is 0, so a mapper
    // that defaulted instead of preserving null would invent a requirement the recruiter never made —
    // and PR 3's engine penalises a candidate for missing a stated one.
    [Fact]
    public void Serialized_AnUnstatedEducationLevelStaysNull()
    {
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(JobPostingResponse.From(BuildBarePosting()), WebOptions));

        json.RootElement.GetProperty("educationLevel").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // The fields that PREDATE this chain keep their old encoding, and this is a deliberate
    // inconsistency with the two names above rather than an oversight. Same trade AnalysisResponse
    // made with `band`: flipping an encoding that already has clients is a repo-wide change, not a
    // scoring one, and a consistent bad convention beats a half-flipped one.
    [Fact]
    public void Serialized_PreChainFieldsKeepTheirOldEncoding()
    {
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(JobPostingResponse.From(BuildFullPosting()), WebOptions));
        var root = json.RootElement;

        root.GetProperty("status").ValueKind.Should().Be(JsonValueKind.Number,
            "JobPostingStatus shipped as an integer before this chain and has clients");
        root.GetProperty("requirements")[0].GetProperty("priority").ValueKind.Should().Be(JsonValueKind.Number,
            "RequirementPriority predates this chain too — only the two NEW enums were named");

        root.GetProperty("id").GetProperty("value").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("ownerId").GetProperty("value").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("companyName").GetProperty("value").GetString().Should().Be("Contoso");
        root.GetProperty("requirements")[0].GetProperty("skill").GetProperty("name").GetString()
            .Should().Be("C#");
    }

    // The full property list, in order, at every level — captured off the live endpoint BEFORE the DTO
    // existed. A field silently disappearing is the failure a per-field assertion cannot see, and
    // "reproduced key for key" is this change's claim.
    [Fact]
    public void Serialized_CarriesExactlyTheFieldsTheEndpointAlreadyShipped()
    {
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(JobPostingResponse.From(BuildFullPosting()), WebOptions));
        var root = json.RootElement;

        NamesOf(root).Should().Equal(
            "id", "ownerId", "title", "description", "companyId", "companyName", "status", "createdAt",
            "updatedAt", "publishedAt", "closesAt", "requirements", "responsibilities",
            "languageRequirements", "educationLevel");

        NamesOf(root.GetProperty("requirements")[0]).Should().Equal("skill", "priority", "weight");
        NamesOf(root.GetProperty("responsibilities")[0]).Should().Equal("description");
        NamesOf(root.GetProperty("languageRequirements")[0]).Should().Equal("name", "minimumLevel");
    }

    // THE LIVE HALF, AND IT NEEDS A STORE THAT STATES SOMETHING. Written this way after the obvious
    // version failed its own control: asserting the live shape against a posting the API can create
    // passes IDENTICALLY with the mapper removed, because for such a posting the DTO and the aggregate
    // serialize key for key — `educationLevel` is null and `languageRequirements` is [] either way.
    // That is exactly why this fix is free today, and it is also why "the live shape is unchanged" is
    // documentation rather than a guard. Only a posting that states the two new fields can tell a
    // mapped endpoint from an unmapped one, and no request can build one.
    //
    // So the store hands back a posting that does, which is what the authoring endpoints will produce
    // in the next phase, and then the wire has to say "Bachelor" and "Professional".
    //
    // Controlled: with the four `JobPostingResponse.From` calls removed, exactly these two cases fail,
    // and they fail with "the target element has type 'Number'" — the integers, back on the wire, read
    // off the response rather than argued from the absence of a JsonStringEnumConverter.
    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "/publish")]
    public async Task TheLiveEndpoints_NameTheTwoNewEnumsOnTheWire(string method, string suffix)
    {
        using var factory = new ApiTestFactory(configureServices: services =>
            services.AddSingleton<IJobPostingRepository>(_ =>
                new RequirementStatingJobPostingRepository(
                    new Infrastructure.Persistence.InMemoryJobPostingRepository())));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var id = (await PostJobAsync(client, token)).GetProperty("id").GetProperty("value").GetGuid();

        var body = await ReadAsync(client, token, new HttpMethod(method), $"/jobs/{id}{suffix}");

        body.GetProperty("educationLevel").GetString().Should().Be("Bachelor",
            "the aggregate would have put the persisted number 2 here");
        body.GetProperty("languageRequirements")[0].GetProperty("minimumLevel").GetString()
            .Should().Be("Professional");
    }

    // The shape a client sees today, on all four routes. This one is NOT a guard on the mapper — see
    // above, it passes with the mapper removed — and it is kept for the property it does prove: the DTO
    // is behaviour-neutral for every posting the API can currently produce, which is the claim that
    // makes landing it now cheap rather than a break.
    [Fact]
    public async Task TheLiveEndpoints_ReproduceTodaysShapeOnCreateGetPublishAndClose()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var created = await PostJobAsync(client, token);
        var id = created.GetProperty("id").GetProperty("value").GetGuid();

        var read = await ReadAsync(client, token, HttpMethod.Get, $"/jobs/{id}");
        var published = await ReadAsync(client, token, HttpMethod.Post, $"/jobs/{id}/publish");
        var closed = await ReadAsync(client, token, HttpMethod.Post, $"/jobs/{id}/close");

        foreach (var (name, body) in new[]
        {
            ("create", created), ("read", read), ("publish", published), ("close", closed)
        })
        {
            NamesOf(body).Should().Equal(
                new[]
                {
                    "id", "ownerId", "title", "description", "companyId", "companyName", "status",
                    "createdAt", "updatedAt", "publishedAt", "closesAt", "requirements",
                    "responsibilities", "languageRequirements", "educationLevel"
                },
                "the {0} response is reproduced key for key", name);

            body.GetProperty("status").ValueKind.Should().Be(JsonValueKind.Number);
            body.GetProperty("educationLevel").ValueKind.Should().Be(JsonValueKind.Null,
                "no endpoint can state one yet — which is what makes naming the enum free today");
            body.GetProperty("languageRequirements").GetArrayLength().Should().Be(0,
                "nor add a language requirement");
        }

        published.GetProperty("status").GetInt32().Should().Be((int)JobPostingStatus.Published);
        closed.GetProperty("status").GetInt32().Should().Be((int)JobPostingStatus.Closed);
    }

    private static IEnumerable<string> NamesOf(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    private static async Task<JsonElement> PostJobAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/jobs")
        {
            Content = JsonContent.Create(new
            {
                title = "Senior Backend Engineer",
                companyName = "Contoso",
                companyId = (Guid?)null,
                description = "Build deterministic scoring systems."
            })
        }.WithBearer(token);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> ReadAsync(
        HttpClient client, string token, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path).WithBearer(token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // Built through the Domain factories because the API cannot: SetEducationLevel and
    // SetLanguageRequirements have no caller in src/, which is the finding this DTO answers.
    private static JobPosting BuildFullPosting()
    {
        var posting = BuildBarePosting();
        posting.SetRequirements([JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave)]);
        posting.SetResponsibilities([Responsibility.Create("Own the scoring engine.")]);
        posting.SetLanguageRequirements(
            [LanguageRequirement.Create("English", LanguageProficiency.Professional)]);
        posting.SetEducationLevel(EducationLevel.Bachelor);
        return posting;
    }

    private static JobPosting BuildBarePosting() =>
        JobPosting.Create(
            AccountId.New(), "Senior Backend Engineer", OrganizationName.Create("Contoso"),
            "Build deterministic scoring systems.");

    // Stands in for the authoring endpoints this phase deliberately does not build. It states the two
    // fields on every posting the store hands out, so a live response can carry them at all — the
    // reason the mapper is otherwise untestable through HTTP.
    //
    // It mutates the instance rather than rebuilding it because JobPosting has no copy constructor and
    // the in-memory store returns the object it was given; a real reload would produce the same posting
    // with the same two values, which is the state being modelled.
    private sealed class RequirementStatingJobPostingRepository(IJobPostingRepository inner) : IJobPostingRepository
    {
        public Task AddAsync(JobPosting jobPosting, CancellationToken cancellationToken = default) =>
            inner.AddAsync(jobPosting, cancellationToken);

        public Task UpdateAsync(JobPosting jobPosting, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(jobPosting, cancellationToken);

        public async Task<JobPosting?> GetByIdAsync(
            JobPostingId id, CancellationToken cancellationToken = default) =>
            Stating(await inner.GetByIdAsync(id, cancellationToken));

        public async Task<Page<JobPosting>> GetPageByOwnerIdAsync(
            AccountId ownerId, PageRequest page, CancellationToken cancellationToken = default)
        {
            var found = await inner.GetPageByOwnerIdAsync(ownerId, page, cancellationToken);
            return new Page<JobPosting>([.. found.Items.Select(posting => Stating(posting)!)], found.NextCursor);
        }

        public async Task<Page<JobPosting>> GetPageByOrganizationIdAsync(
            OrganizationId organizationId, PageRequest page, CancellationToken cancellationToken = default)
        {
            var found = await inner.GetPageByOrganizationIdAsync(organizationId, page, cancellationToken);
            return new Page<JobPosting>([.. found.Items.Select(posting => Stating(posting)!)], found.NextCursor);
        }

        private static JobPosting? Stating(JobPosting? posting)
        {
            if (posting is null)
                return null;

            posting.SetEducationLevel(EducationLevel.Bachelor);
            posting.SetLanguageRequirements(
                [LanguageRequirement.Create("English", LanguageProficiency.Professional)]);
            return posting;
        }
    }
}
