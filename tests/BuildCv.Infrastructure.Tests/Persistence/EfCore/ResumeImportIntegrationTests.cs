using BuildCv.Application.Resumes;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// The import path against a REAL SCHEMA, which neither of the other two layers can reach: the Api tests
// run on the in-memory provider and the handler tests on a hand-written fake, and neither has column
// types, lengths or an encryption round trip.
//
// That gap is where a live defect lived. Language.Name is the only plaintext BOUNDED column on the
// aggregate — nvarchar(100) — so a 101-character name passed every unit test, reached SQL Server as
// error 2628 ("String or binary data would be truncated"), was not translated by
// SaveChangesExtensions (which knows 2601 and 2627), and became a 500 with the candidate's own text
// written into the log by GlobalExceptionHandler.
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ResumeImportIntegrationTests
{
    private readonly SqlServerFixture _fixture;

    public ResumeImportIntegrationTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Import_AFullDraft_PersistsEveryCollectionInOneWrite()
    {
        var owner = AccountId.New();
        ResumeId storedId;

        await using (var writer = _fixture.NewApplicationContext())
        {
            var handler = new CreateResumeFromDraftHandler(TestRepositories.Resumes(writer));
            var result = await handler.Handle(new CreateResumeFromDraftCommand(owner, FullDraft()));

            result.FieldErrors.Should().BeEmpty();
            result.IsSuccess.Should().BeTrue();
            storedId = result.Resume!.Id;
        }

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.Resumes(reader).GetByIdAsync(storedId);

        reloaded.Should().NotBeNull();
        reloaded!.Experiences.Should().ContainSingle();
        reloaded.Educations.Should().ContainSingle();
        reloaded.Skills.Should().ContainSingle();
        reloaded.Projects.Should().ContainSingle();
        reloaded.Certificates.Should().ContainSingle();
        reloaded.Languages.Should().ContainSingle();
        reloaded.Awards.Should().ContainSingle();
        reloaded.Publications.Should().ContainSingle();
        reloaded.Interests.Should().ContainSingle();
        reloaded.References.Should().ContainSingle();

        // The two fields this endpoint made reachable, through the encrypted columns that store them.
        reloaded.ContactInformation.Website!.Value.Should().Be("https://jane.example.com");
        reloaded.ContactInformation.Profiles.Should().ContainSingle()
            .Which.Network.Should().Be("GitHub");
    }

    // A name one character over its column. It must be refused by the validator with a field path, not
    // discovered by SQL Server — this is the assertion that would have caught the 2628.
    [Fact]
    public async Task Import_WithALanguageNameOverItsColumnLength_IsAFieldErrorAndNeverReachesTheDatabase()
    {
        await using var writer = _fixture.NewApplicationContext();
        var handler = new CreateResumeFromDraftHandler(TestRepositories.Resumes(writer));

        var draft = FullDraft() with { Languages = [new LanguageDraft(new string('a', 101))] };

        var act = async () => await handler.Handle(new CreateResumeFromDraftCommand(AccountId.New(), draft));
        var result = await act.Should().NotThrowAsync(
            "a value too long for its column must be a 400 with a field path, not a DbUpdateException");

        result.Subject.IsSuccess.Should().BeFalse();
        result.Subject.FieldErrors.Should().ContainSingle()
            .Which.Should().Be(new FieldError("languages[0].name", "Language name exceeds 100 characters."));
    }

    // The other side of the same boundary: exactly at the column length still round-trips through
    // nvarchar(100), so the Domain rule and the column agree rather than the rule merely being stricter.
    [Fact]
    public async Task Import_WithALanguageNameExactlyAtItsColumnLength_RoundTrips()
    {
        var owner = AccountId.New();
        var name = new string('a', 100);
        ResumeId storedId;

        await using (var writer = _fixture.NewApplicationContext())
        {
            var handler = new CreateResumeFromDraftHandler(TestRepositories.Resumes(writer));
            var result = await handler.Handle(new CreateResumeFromDraftCommand(
                owner, FullDraft() with { Languages = [new LanguageDraft(name, "Bilingüe", "Native")] }));

            result.FieldErrors.Should().BeEmpty();
            storedId = result.Resume!.Id;
        }

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.Resumes(reader).GetByIdAsync(storedId);

        reloaded!.Languages.Should().ContainSingle().Which.Name.Should().Be(name);
    }

    private static ResumeDraft FullDraft() => new(
        Contact: new ContactDraft(
            FullName: "Jane Candidate",
            Email: $"{Guid.NewGuid():N}@example.com",
            PhoneNumber: "+541155550123",
            Location: "Buenos Aires",
            Website: "https://jane.example.com",
            Summary: "Backend engineer.",
            Profiles: [new ProfileDraft("GitHub", "janedev", "https://github.com/janedev")]),
        Experiences:
        [
            new ExperienceDraft("Professional", "Mercado Libre", "Senior Engineer",
                "2019-03-01", "2023-06-30", "Payments platform.", ["Cut latency in half"])
        ],
        Educations:
        [
            new EducationDraft("Universidad de Buenos Aires", "Ingeniero en Sistemas", "Software",
                "2012-03-01", "2017-12-01", "8.4", "Bachelor")
        ],
        Skills: [new SkillDraft("C#", "Advanced", "7")],
        Projects:
        [
            new ProjectDraft("buildcv", "2024-01-01", null, "A CV scorer.",
                "https://github.com/janedev/buildcv", "https://buildcv.example.com",
                ["dotnet"], ["Deterministic scoring"])
        ],
        Certificates:
        [
            new CertificateDraft("AWS Solutions Architect", "Amazon", "cred-123",
                "https://aws.example.com/cred-123", "2024-01-01", "2027-01-01")
        ],
        Languages: [new LanguageDraft("Español", "Bilingüe", "Native")],
        Awards: [new AwardDraft("Best Hack", "Hackathon AR", "2021-11-05", "First place.")],
        Publications:
        [
            new PublicationDraft("On Scoring", "ACM", "https://acm.example.com/on-scoring",
                "2022-05-01", "A paper.")
        ],
        Interests: [new InterestDraft("Climbing", ["bouldering"])],
        References:
        [
            new ReferenceDraft("John Manager", "Engineering Manager", "Mercado Libre",
                "john@example.com", "+541155550999", "Would hire again.")
        ]);
}
