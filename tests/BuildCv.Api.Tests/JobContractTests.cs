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

    // The v1 settlement on the fields this DTO used to defer: both remaining enums carry names, ids
    // are bare guids, and the single-value wrappers are gone — `companyName` answers "Contoso", not
    // {"value": "Contoso"}, and `skill` answers "C#", not {"name": "C#"}, which is what the
    // /v1/job-offers extract and import sides already said. Bare means bare: GetGuid()/GetString() on
    // the property itself throws if a wrapper object ever comes back.
    [Fact]
    public void Serialized_EnumsCarryNamesAndIdsAndWrappedStringsAreBare()
    {
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(JobPostingResponse.From(BuildFullPosting()), WebOptions));
        var root = json.RootElement;

        root.GetProperty("status").GetString().Should().Be("Draft",
            "every enum on the v1 wire carries its name — JobPostingStatus was one of the two integers left");
        root.GetProperty("requirements")[0].GetProperty("priority").GetString().Should().Be("MustHave",
            "RequirementPriority was the other");

        root.GetProperty("id").GetGuid().Should().NotBeEmpty();
        root.GetProperty("ownerId").GetGuid().Should().NotBeEmpty();
        root.GetProperty("companyName").GetString().Should().Be("Contoso");
        root.GetProperty("requirements")[0].GetProperty("skill").GetString().Should().Be("C#");
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

    // THE LIVE HALF FOR THE TWO FIELDS NO REQUEST CAN SET, AND IT NEEDS A STORE THAT STATES THEM.
    // `educationLevel` is null and `languageRequirements` is [] on every posting the API can create —
    // CreateJobRequest carries no field for either and there is no update endpoint — so only a store
    // handing back a posting that states both (what the authoring endpoints will produce in a later
    // phase) can prove the live wire says "Bachelor" and "Professional" rather than the persisted
    // numbers. Since the v1 unwrap, the OTHER fields distinguish a mapped endpoint from an unmapped
    // one on any posting — a bare-posting live test is no longer blind to a removed mapper — but
    // these two stay unreachable without the decorated store, which is why it remains.
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

        var id = (await PostJobAsync(client, token)).GetProperty("id").GetGuid();

        var body = await ReadAsync(client, token, new HttpMethod(method), $"/v1/jobs/{id}{suffix}");

        body.GetProperty("educationLevel").GetString().Should().Be("Bachelor",
            "the aggregate would have put the persisted number 2 here");
        body.GetProperty("languageRequirements")[0].GetProperty("minimumLevel").GetString()
            .Should().Be("Professional");
    }

    // The v1 shape a client sees, on all four routes: the full key list, `status` as a name on every
    // response, and null `educationLevel` staying null. Unlike its pre-v1 ancestor this IS a mapper
    // guard — an endpoint returning the aggregate now ships enveloped ids and a numeric status, and
    // the assertions below refuse both.
    [Fact]
    public async Task TheLiveEndpoints_ServeTheV1ShapeOnCreateGetPublishAndClose()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var created = await PostJobAsync(client, token);
        var id = created.GetProperty("id").GetGuid();

        var read = await ReadAsync(client, token, HttpMethod.Get, $"/v1/jobs/{id}");
        var published = await ReadAsync(client, token, HttpMethod.Post, $"/v1/jobs/{id}/publish");
        var closed = await ReadAsync(client, token, HttpMethod.Post, $"/v1/jobs/{id}/close");

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

            body.GetProperty("status").ValueKind.Should().Be(JsonValueKind.String);
            body.GetProperty("educationLevel").ValueKind.Should().Be(JsonValueKind.Null,
                "no endpoint can state one yet — which is what makes naming the enum free today");
            body.GetProperty("languageRequirements").GetArrayLength().Should().Be(0,
                "nor add a language requirement");
        }

        published.GetProperty("status").GetString().Should().Be(nameof(JobPostingStatus.Published));
        closed.GetProperty("status").GetString().Should().Be(nameof(JobPostingStatus.Closed));
    }

    private static IEnumerable<string> NamesOf(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    private static async Task<JsonElement> PostJobAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs")
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
