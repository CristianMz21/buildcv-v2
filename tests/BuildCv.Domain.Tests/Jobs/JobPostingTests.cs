using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Jobs;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Jobs;

public class JobPostingTests
{
    [Fact]
    public void JobPosting_with_requirements_can_be_created()
    {
        var job = new JobPosting(
            Title: "Senior .NET Developer",
            Company: OrganizationName.Create("TechCorp"),
            Description: "Seeking experienced .NET developer")
        {
            Requirements =
            [
                new JobRequirement("C#", RequirementPriority.MustHave, 2.0),
                new JobRequirement("SQL Server", RequirementPriority.MustHave, 1.5),
                new JobRequirement("Docker", RequirementPriority.NiceToHave, 1.0)
            ]
        };

        job.Title.Should().Be("Senior .NET Developer");
        job.Requirements.Should().HaveCount(3);
        job.Requirements[0].Weight.Should().Be(2.0);
    }

    [Fact]
    public void JobPosting_with_defaults_can_be_created()
    {
        var job = new JobPosting(
            Title: "Junior .NET Developer",
            Company: OrganizationName.Create("TechCorp"));

        job.Requirements.Should().BeEmpty();
        job.Description.Should().BeNull();
    }

    [Fact]
    public void JobPosting_is_immutable()
    {
        var job1 = new JobPosting(
            Title: "Junior .NET Developer",
            Company: OrganizationName.Create("TechCorp"));

        var job2 = job1 with { Title = "Senior .NET Developer" };

        job1.Title.Should().Be("Junior .NET Developer");
        job2.Title.Should().Be("Senior .NET Developer");
    }
}
