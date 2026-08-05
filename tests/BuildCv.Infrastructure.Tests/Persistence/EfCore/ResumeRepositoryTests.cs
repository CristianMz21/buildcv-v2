using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Persistence.Conventions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ResumeRepositoryTests
{
    private readonly SqlServerFixture _fixture;

    public ResumeRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    // The claim ResumeRepository makes in its own header comment, checked instead of trusted: owned
    // collections arrive with their principal and no Include is needed. If EF ever stopped auto-including
    // them, the repository would silently start returning resumes with ten empty lists.
    [Fact]
    public async Task GetByIdAsync_LoadsEveryOwnedCollectionWithoutAnInclude()
    {
        var resume = FullResume(AccountId.New());

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Resumes(writer).AddAsync(resume);

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.Resumes(reader).GetByIdAsync(resume.Id);

        reloaded.Should().NotBeNull();
        reloaded!.ContactInformation.FullName.Value.Should().Be("Ada Lovelace");
        reloaded.Experiences.Should().ContainSingle();
        reloaded.Educations.Should().ContainSingle();
        reloaded.Skills.Should().HaveCount(2);
        reloaded.Projects.Should().ContainSingle();
        reloaded.Certificates.Should().ContainSingle();
        reloaded.Languages.Should().ContainSingle();
        reloaded.Awards.Should().ContainSingle();
        reloaded.Publications.Should().ContainSingle();
        reloaded.Interests.Should().ContainSingle();
        reloaded.References.Should().ContainSingle();
    }

    // Also the NO-TRACKING half of the owned-collection guarantee, which GetByIdAsync cannot cover: that
    // one is AsTracking(), this one rides the context-wide NoTracking default, and NoTracking is exactly
    // where EF's identity resolution behaves differently for owned types. So `second` is a fully
    // populated resume rather than a bare one — otherwise the list path could return ten empty
    // collections and nothing here would notice.
    //
    // It is also the guarantee the KEYSET PROJECTION could quietly break. The paged query no longer
    // selects the entity on its own: it selects the entity paired with its shadow Seq, because Seq is
    // the next cursor and a materialized Resume can no longer be asked for it. If EF stopped carrying
    // owned navigations through that projection, every resume in every list would arrive with ten empty
    // collections and nothing else here would notice.
    [Fact]
    public async Task GetPageByOwnerIdAsync_ReturnsOnlyThatOwnersResumes_NewestFirst_WithTheirOwnedCollections()
    {
        var owner = AccountId.New();
        var other = AccountId.New();

        var first = Minimal(owner, "first");
        var second = FullResume(owner);

        await using (var writer = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.Resumes(writer);
            await repository.AddAsync(first);
            await repository.AddAsync(second);
            await repository.AddAsync(Minimal(other, "other"));
        }

        await using var reader = _fixture.NewApplicationContext();
        var mine = (await TestRepositories.Resumes(reader).GetPageByOwnerIdAsync(owner, PageRequests.Of())).Items;

        mine.Select(resume => resume.Id).Should().Equal(second.Id, first.Id);

        var populated = mine[0];
        populated.ContactInformation.FullName.Value.Should().Be("Ada Lovelace");
        populated.Experiences.Should().ContainSingle();
        populated.Educations.Should().ContainSingle();
        populated.Skills.Should().HaveCount(2);
        populated.Projects.Should().ContainSingle();
        populated.Certificates.Should().ContainSingle();
        populated.Languages.Should().ContainSingle();
        populated.Awards.Should().ContainSingle();
        populated.Publications.Should().ContainSingle();
        populated.Interests.Should().ContainSingle();
        populated.References.Should().ContainSingle();

        mine[1].Skills.Should().BeEmpty("the bare resume really has no children, so the assertion above discriminates");
    }

    [Fact]
    public async Task UpdateAsync_WithAnAggregateThisRepositoryDidNotLoad_RefusesToWriteIt()
    {
        var resume = Minimal(AccountId.New(), "detached");

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Resumes(writer).AddAsync(resume);

        // A different context, so the instance is detached here: it carries no rowversion, and Update()
        // would additionally mark all ten owned collections Added. Refused rather than attempted.
        await using var other = _fixture.NewApplicationContext();

        var act = async () => await TestRepositories.Resumes(other).UpdateAsync(resume);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*instance returned by this repository*");
    }

    // The owned-collection diff case: EF has to work out that one child row is new and another is gone,
    // across a context that never saw the original graph.
    [Fact]
    public async Task UpdateAsync_AddsAndRemovesChildEntriesAcrossContextInstances()
    {
        var resume = Minimal(AccountId.New(), "diff");
        resume.AddSkill(Skill.Create(Technology.Create("Fortran"), SkillLevel.Advanced, 5));
        resume.AddSkill(Skill.Create(Technology.Create("Ada"), SkillLevel.Beginner, 1));

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Resumes(writer).AddAsync(resume);

        await using (var mutator = _fixture.NewApplicationContext())
        {
            var repository = TestRepositories.Resumes(mutator);
            var loaded = await repository.GetByIdAsync(resume.Id);
            loaded!.RemoveSkill("Fortran");
            loaded.AddSkill(Skill.Create(Technology.Create("Lisp"), SkillLevel.Intermediate, 3));
            loaded.AddLanguage(Language.Create("Spanish", "Native"));
            await repository.UpdateAsync(loaded);
        }

        await using var reader = _fixture.NewApplicationContext();
        var reloaded = await TestRepositories.Resumes(reader).GetByIdAsync(resume.Id);

        reloaded!.Skills.Select(skill => skill.Name.Name).Should().BeEquivalentTo(["Ada", "Lisp"]);
        reloaded.Languages.Should().ContainSingle().Which.Name.Should().Be("Spanish");
    }

    // DeleteAsync is a tombstone, not a DELETE, and it has to keep the whole aggregate: the ten owned
    // collections are cascade-marked Deleted the instant the root is, and only the audit interceptor
    // rescuing them stops SaveChanges from destroying everything but the header row.
    [Fact]
    public async Task DeleteAsync_TombstonesTheResumeAndKeepsItsChildren()
    {
        var resume = FullResume(AccountId.New());

        await using (var writer = _fixture.NewApplicationContext())
            await TestRepositories.Resumes(writer).AddAsync(resume);

        await using (var deleter = _fixture.NewApplicationContext())
            await TestRepositories.Resumes(deleter).DeleteAsync(resume.Id);

        await using var reader = _fixture.NewApplicationContext();

        (await TestRepositories.Resumes(reader).GetByIdAsync(resume.Id)).Should().BeNull();
        (await TestRepositories.Resumes(reader).GetPageByOwnerIdAsync(resume.OwnerId, PageRequests.Of()))
            .Items.Should().BeEmpty();

        var tombstoned = await reader.Resumes.AsTracking()
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == resume.Id);

        reader.Entry(tombstoned).Property(ShadowColumns.DeletedAt).CurrentValue.Should().NotBeNull();
        tombstoned.Skills.Should().HaveCount(2, "a tombstone preserves the aggregate, it does not gut it");
        tombstoned.ContactInformation.FullName.Value.Should().Be("Ada Lovelace");
    }

    // The DECISION recorded in ResumeRepository.CascadeToAnalysesAsync. Analysis has no foreign key to
    // Resumes, so nothing in the schema or the query filter reaches it: without this cascade, deleting a
    // resume would hide the resume and leave every score derived from it readable and joinable by
    // ResumeId forever.
    [Fact]
    public async Task DeleteAsync_AlsoTombstonesTheAnalysesDerivedFromTheResume()
    {
        var resume = Minimal(AccountId.New(), "cascade");
        var analysis = NewAnalysis(resume.Id);
        var unrelated = NewAnalysis(ResumeId.New());

        await using (var writer = _fixture.NewApplicationContext())
        {
            await TestRepositories.Resumes(writer).AddAsync(resume);
            await TestRepositories.Analyses(writer).AddAsync(analysis);
            await TestRepositories.Analyses(writer).AddAsync(unrelated);
        }

        await using (var deleter = _fixture.NewApplicationContext())
            await TestRepositories.Resumes(deleter).DeleteAsync(resume.Id);

        await using var reader = _fixture.NewApplicationContext();
        var analyses = TestRepositories.Analyses(reader);

        (await analyses.GetPageByResumeIdAsync(resume.Id, PageRequests.Of())).Items.Should().BeEmpty();
        (await analyses.GetPageByResumeIdAsync(unrelated.ResumeId, PageRequests.Of())).Items.Should().ContainSingle(
            "only the deleted resume's analyses are tombstoned");

        // Tombstoned, not destroyed: the score history survives for audit exactly as the resume does.
        var retained = await reader.Analyses.IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == analysis.Id);
        retained.Breakdown.Should().Be(analysis.Breakdown);
    }

    // THE SAME DECISION, applied to the second aggregate keyed by ResumeId — and the argument is if
    // anything stronger. A readability recommendation's Message quotes the candidate's own bullet points
    // and job titles, so a report left behind after "delete my resume" is not merely joinable derived
    // data: it is a readable fragment of the document they asked to have removed.
    //
    // Read back through IgnoreQueryFilters rather than through the port, because
    // IReadabilityReportRepository has no read method yet — which is exactly why this cascade would
    // otherwise be invisible to every test in the suite.
    [Fact]
    public async Task DeleteAsync_AlsoTombstonesTheReadabilityReportsDerivedFromTheResume()
    {
        var resume = Minimal(AccountId.New(), "readability-cascade");
        var report = NewReadabilityReport(resume.Id);
        var unrelated = NewReadabilityReport(ResumeId.New());

        await using (var writer = _fixture.NewApplicationContext())
        {
            await TestRepositories.Resumes(writer).AddAsync(resume);
            await TestRepositories.ReadabilityReports(writer).AddAsync(report);
            await TestRepositories.ReadabilityReports(writer).AddAsync(unrelated);
        }

        await using (var deleter = _fixture.NewApplicationContext())
            await TestRepositories.Resumes(deleter).DeleteAsync(resume.Id);

        // NewContext, not NewApplicationContext: the shadow assertion below reads DeletedAt off the
        // CHANGE TRACKER, and the application-shaped context is NoTracking — Entry() on an untracked
        // instance answers the property's default, which is null, so the assertion would have failed for
        // a tombstone that really was written. Measured: it did.
        await using var reader = _fixture.NewContext();

        (await reader.ReadabilityReports.AnyAsync(entity => entity.Id == report.Id))
            .Should().BeFalse("the query filter hides a tombstoned report");
        (await reader.ReadabilityReports.AnyAsync(entity => entity.Id == unrelated.Id))
            .Should().BeTrue("only the deleted resume's reports are tombstoned");

        // Tombstoned, not destroyed: the report survives for audit exactly as the resume does.
        var retained = await reader.ReadabilityReports.IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == report.Id);
        reader.Entry(retained).Property(ShadowColumns.DeletedAt).CurrentValue.Should().NotBeNull();
        retained.Breakdown.Should().Be(report.Breakdown);
    }

    [Fact]
    public async Task DeleteAsync_ForAResumeThatIsNotThere_IsANoOp()
    {
        await using var context = _fixture.NewApplicationContext();

        var act = async () => await TestRepositories.Resumes(context).DeleteAsync(ResumeId.New());

        await act.Should().NotThrowAsync();
    }

    private static Resume Minimal(AccountId ownerId, string label) =>
        Resume.Create(ownerId, new ContactInformation(
            PersonName.Create("Test Person"), Email.Create($"{label}.{Guid.NewGuid():N}@example.com")));

    private static Resume FullResume(AccountId ownerId)
    {
        var contact = new ContactInformation(
            PersonName.Create("Ada Lovelace"),
            Email.Create($"resume.{Guid.NewGuid():N}@example.com"),
            PhoneNumber.Create("+541155551234"),
            "Buenos Aires, AR",
            Url.Create("https://ada.example.com"),
            "Analytical engine specialist.")
        {
            Profiles = [new Profile("GitHub", "ada", Url.Create("https://github.com/ada"))],
        };

        var resume = Resume.Create(ownerId, contact);
        var period = DateRange.Create(new DateOnly(2020, 1, 1), new DateOnly(2023, 6, 30));

        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Analytical Engines SA"),
            "Principal Engineer",
            period,
            "Led the compiler team."));
        resume.AddEducation(new Education(
            OrganizationName.Create("University of London"), "BSc", "Mathematics", period, "First"));
        resume.AddSkill(Skill.Create(Technology.Create("C#"), SkillLevel.Expert, 10));
        resume.AddSkill(Skill.Create(Technology.Create("SQL"), SkillLevel.Advanced, 8));
        resume.AddProject(new Project("Difference Engine", period, "A mechanical computer."));
        resume.AddCertificate(new Certificate(
            "Azure Architect", OrganizationName.Create("Microsoft"), "CRED-123", null, period));
        resume.AddLanguage(Language.Create("English", "Native"));
        resume.AddAward(new Award(
            "Turing Award", OrganizationName.Create("ACM"), new DateOnly(2022, 3, 1), "For services."));
        resume.AddPublication(new Publication(
            "Notes on the Engine", OrganizationName.Create("Scientific Memoirs"), null,
            new DateOnly(1843, 10, 1), "The first algorithm."));
        resume.AddInterest(new Interest("Mathematics"));
        resume.AddReference(new Reference(
            "Charles Babbage", "Professor", OrganizationName.Create("Cambridge"), null, null, "Recommended."));

        return resume;
    }

    private static Analysis NewAnalysis(ResumeId resumeId) =>
        Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.5, 0.4, ScoringWeightsSnapshot.Default()),
            resumeId,
            JobPostingId.New(),
            DateTimeOffset.UtcNow,
            [
                Recommendation.Create(
                    SectionType.Projects, RecommendationPriority.Important,
                    RecommendationKind.FewerProjectsThanExpected, "Add more C# projects.", 0.05),
            ]);

    private static ReadabilityReport NewReadabilityReport(ResumeId resumeId) =>
        ReadabilityReport.Create(
            ReadabilityReportId.New(),
            ReadabilityBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.0, ReadabilityWeightsSnapshot.Default()),
            resumeId,
            DateTimeOffset.UtcNow,
            [
                ReadabilityRecommendation.Create(
                    ReadabilitySectionType.Contact, RecommendationPriority.Important,
                    ReadabilityRecommendationKind.NoPhoneNumber, "Add a phone number.", 0.05),
            ]);
}
