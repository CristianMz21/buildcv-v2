using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Jobs;

public class JobPostingTests
{
    private static readonly AccountId OwnerId = AccountId.New();

    [Fact]
    public void JobPosting_personal_can_be_created_with_requirements()
    {
        var job = JobPosting.Create(
            ownerId: OwnerId,
            title: "Senior .NET Developer",
            companyName: OrganizationName.Create("TechCorp"),
            description: "Seeking experienced .NET developer");

        job.AddRequirement(JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave, 2.0));
        job.AddRequirement(JobRequirement.Create(Technology.Create("SQL Server"), RequirementPriority.MustHave, 1.5));
        job.AddRequirement(JobRequirement.Create(Technology.Create("Docker"), RequirementPriority.NiceToHave, 1.0));

        job.Title.Should().Be("Senior .NET Developer");
        job.Requirements.Should().HaveCount(3);
        job.Requirements[0].Weight.Should().Be(2.0);
        job.CompanyName!.Value.Should().Be("TechCorp");
        job.CompanyId.Should().BeNull();
        job.Status.Should().Be(JobPostingStatus.Draft);
    }

    [Fact]
    public void JobPosting_with_defaults_can_be_created()
    {
        var job = JobPosting.Create(
            ownerId: OwnerId,
            title: "Junior .NET Developer",
            companyName: OrganizationName.Create("TechCorp"));

        job.Requirements.Should().BeEmpty();
        job.Description.Should().BeNull();
    }

    [Fact]
    public void JobPosting_for_organization_does_not_store_company_name()
    {
        var orgId = OrganizationId.New();

        var job = JobPosting.CreateForOrganization(
            ownerId: OwnerId,
            companyOrgId: orgId,
            title: "Senior .NET Developer");

        job.CompanyId.Should().Be(orgId);
        job.CompanyName.Should().BeNull();
    }

    [Fact]
    public void JobPosting_publish_close_archive_lifecycle()
    {
        var job = JobPosting.Create(OwnerId, "Dev", OrganizationName.Create("TechCorp"));

        job.Publish();
        job.Status.Should().Be(JobPostingStatus.Published);
        job.PublishedAt.Should().NotBeNull();

        job.Close();
        job.Status.Should().Be(JobPostingStatus.Closed);
        job.ClosesAt.Should().NotBeNull();

        job.Archive();
        job.Status.Should().Be(JobPostingStatus.Archived);
    }

    [Fact]
    public void JobPosting_publish_only_from_draft()
    {
        var job = JobPosting.Create(OwnerId, "Dev", OrganizationName.Create("TechCorp"));
        job.Publish();

        var act = () => job.Publish();

        act.Should().Throw<InvalidJobPostingException>();
    }

    [Fact]
    public void JobPosting_rejects_duplicate_skills()
    {
        var job = JobPosting.Create(OwnerId, "Dev", OrganizationName.Create("TechCorp"));
        job.AddRequirement(JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave));

        var act = () => job.AddRequirement(JobRequirement.Create(Technology.Create("c#"), RequirementPriority.NiceToHave));

        act.Should().Throw<DuplicateSkillException>();
    }

    [Fact]
    public void JobPosting_rejects_empty_title()
    {
        var act = () => JobPosting.Create(OwnerId, "   ", OrganizationName.Create("TechCorp"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void JobPosting_rejects_overlong_title()
    {
        var act = () => JobPosting.Create(OwnerId, new string('a', 201), OrganizationName.Create("TechCorp"));

        act.Should().Throw<InvalidJobPostingException>();
    }

    [Fact]
    public void JobPosting_equality_by_id()
    {
        var job = JobPosting.Create(OwnerId, "Dev", OrganizationName.Create("TechCorp"));

        job.Equals(job).Should().BeTrue();
        JobPosting.Create(OwnerId, "Dev2", OrganizationName.Create("TechCorp")).Equals(job).Should().BeFalse();
    }

    [Fact]
    public void JobPosting_requirements_view_reflects_subsequent_additions()
    {
        var job = JobPosting.Create(OwnerId, "Dev", OrganizationName.Create("TechCorp"));
        var view = job.Requirements;

        job.AddRequirement(JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave));

        view.Should().HaveCount(1);
    }

    [Fact]
    public void JobPosting_accepts_language_requirements()
    {
        var job = JobPosting.Create(OwnerId, "Dev", OrganizationName.Create("TechCorp"));

        job.SetLanguageRequirements(
        [
            LanguageRequirement.Create("English", LanguageProficiency.Professional),
            LanguageRequirement.Create("Spanish", LanguageProficiency.Native),
        ]);

        job.LanguageRequirements.Should().HaveCount(2);
        job.LanguageRequirements[0].MinimumLevel.Should().Be(LanguageProficiency.Professional);
    }

    // The guard that matters, and the reason it cannot lean on record equality: LanguageRequirement
    // stores its name as typed, so "English" and "english" are two distinct values. Without an
    // OrdinalIgnoreCase comparison a posting would carry both, and PR 3 would score the candidate
    // against whichever it read first.
    [Fact]
    public void JobPosting_rejects_a_duplicate_language_requirement_case_insensitively()
    {
        var job = JobPosting.Create(OwnerId, "Dev", OrganizationName.Create("TechCorp"));
        job.AddLanguageRequirement(LanguageRequirement.Create("English", LanguageProficiency.Professional));

        var act = () => job.AddLanguageRequirement(
            LanguageRequirement.Create("ENGLISH", LanguageProficiency.Basic));

        act.Should().Throw<DuplicateSkillException>();
        job.LanguageRequirements.Should().ContainSingle();
    }

    // The same guard on the bulk setter. Add-one and set-many are separate code paths on JobPosting,
    // so a guard on only one of them leaves the other as a way in.
    [Fact]
    public void JobPosting_rejects_a_duplicate_within_a_language_requirement_set()
    {
        var job = JobPosting.Create(OwnerId, "Dev", OrganizationName.Create("TechCorp"));

        var act = () => job.SetLanguageRequirements(
        [
            LanguageRequirement.Create("English", LanguageProficiency.Professional),
            LanguageRequirement.Create("english", LanguageProficiency.Basic),
        ]);

        act.Should().Throw<DuplicateSkillException>();
        job.LanguageRequirements.Should().BeEmpty("a rejected set must not leave a partial write behind");
    }

    [Fact]
    public void JobPosting_language_requirements_view_reflects_subsequent_additions()
    {
        var job = JobPosting.Create(OwnerId, "Dev", OrganizationName.Create("TechCorp"));
        var view = job.LanguageRequirements;

        job.AddLanguageRequirement(LanguageRequirement.Create("English", LanguageProficiency.Basic));

        view.Should().HaveCount(1);
    }

    // Not stated has to stay distinguishable from HighSchool = 0, or PR 3 invents a requirement no
    // posting made and penalises every candidate who does not meet it.
    [Fact]
    public void JobPosting_states_no_education_level_by_default() =>
        JobPosting.Create(OwnerId, "Dev", OrganizationName.Create("TechCorp"))
            .EducationLevel.Should().BeNull();

    // The numbers are not arbitrary: they are exactly what ScoringEngine.ComputeSkillsScore has always
    // computed inline from Priority. Changing either one here moves every skills score in the product,
    // which is why they are asserted as literals rather than derived from the enum.
    [Theory]
    [InlineData(RequirementPriority.MustHave, 1.0)]
    [InlineData(RequirementPriority.NiceToHave, 0.5)]
    public void JobRequirement_weight_defaults_from_priority(RequirementPriority priority, double expected) =>
        JobRequirement.Create(Technology.Create("C#"), priority).Weight.Should().Be(expected);

    // Weight is the magnitude and Priority the gate, so a caller who states a magnitude keeps it. Zero
    // is included because it is the one explicit value a `weight ?? default` would swallow if the
    // parameter were ever typed as a plain double with a sentinel.
    [Theory]
    [InlineData(RequirementPriority.MustHave, 0.0)]
    [InlineData(RequirementPriority.MustHave, 2.5)]
    [InlineData(RequirementPriority.NiceToHave, 3.0)]
    public void JobRequirement_explicit_weight_overrides_the_priority_default(
        RequirementPriority priority, double weight) =>
        JobRequirement.Create(Technology.Create("C#"), priority, weight).Weight.Should().Be(weight);

    [Theory]
    [InlineData(-0.1)]
    [InlineData(10.1)]
    public void JobRequirement_rejects_a_weight_outside_the_allowed_range(double weight)
    {
        var act = () => JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave, weight);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // Persisted as tinyint (JobPostingConfiguration). Renumbering a member rewrites the meaning of
    // every row already on disk, so the numbers are pinned rather than left to declaration order.
    [Theory]
    [InlineData(RequirementPriority.MustHave, 0)]
    [InlineData(RequirementPriority.NiceToHave, 1)]
    public void RequirementPriority_members_keep_their_persisted_numbers(
        RequirementPriority priority, int expected) =>
        ((int)priority).Should().Be(expected);
}
