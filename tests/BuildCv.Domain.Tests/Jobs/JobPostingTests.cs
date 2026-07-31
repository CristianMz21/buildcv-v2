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
}
