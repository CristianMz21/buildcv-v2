using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Identity;

public class IdTests
{
    [Fact]
    public void AccountId_rejects_empty_guid()
    {
        var act = () => new AccountId(Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AccountId_new_generates_non_empty()
    {
        var id = AccountId.New();

        id.Value.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void OrganizationId_rejects_empty_guid()
    {
        var act = () => new OrganizationId(Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ResumeId_rejects_empty_guid()
    {
        var act = () => new ResumeId(Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void JobPostingId_rejects_empty_guid()
    {
        var act = () => new JobPostingId(Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AnalysisId_rejects_empty_guid()
    {
        var act = () => new AnalysisId(Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }
}
