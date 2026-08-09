using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Identity;

// THE EXCEPTION TYPE IS THE POINT, not merely that the constructor refuses.
//
// These six threw a bare ArgumentException, which matched no IExceptionHandler branch and turned every
// by-id route into a 500 — and ArgumentException.Message appends "(Parameter 'value')", so a C#
// parameter name reached the response body. EmptyIdentifierException is a DomainException, which the
// Api already answers as a 400 and which Application handlers already catch into a Result.
//
// Asserting `Throw<ArgumentException>` would keep passing if somebody reverted the type, because
// EmptyIdentifierException is NOT one — the assertion below would fail on the revert, which is what
// makes it a pin rather than a restatement of "it throws something".
public class IdTests
{
    public static TheoryData<string, Action> EmptyIdConstructions => new()
    {
        { "AccountId", () => _ = new AccountId(Guid.Empty) },
        { "OrganizationId", () => _ = new OrganizationId(Guid.Empty) },
        { "ResumeId", () => _ = new ResumeId(Guid.Empty) },
        { "JobPostingId", () => _ = new JobPostingId(Guid.Empty) },
        { "AnalysisId", () => _ = new AnalysisId(Guid.Empty) },
        // Moved here from ReadabilityReportTests, which is about the aggregate rather than its id. The
        // version there asserted `.WithParameterName("value")` — it was pinning the leak in place.
        { "ReadabilityReportId", () => _ = new ReadabilityReportId(Guid.Empty) }
    };

    [Theory]
    [MemberData(nameof(EmptyIdConstructions))]
    public void An_id_rejects_the_empty_guid_as_a_domain_invariant(string name, Action construct)
    {
        var thrown = construct.Should().Throw<EmptyIdentifierException>()
            .WithMessage($"{name} must not be empty.").Which;

        // THE MESSAGE CARRIES NO C# PARAMETER NAME. This is the assertion that would have caught the
        // original defect: ArgumentException(message, paramName) reads back as
        // "AnalysisId must not be empty. (Parameter 'value')", and that string reached a response detail
        // and an error log on every by-id route.
        thrown.Message.Should().NotContain("Parameter",
            "an internal parameter name is not something to put in front of a client");
    }

    [Fact]
    public void AccountId_new_generates_non_empty() => AccountId.New().Value.Should().NotBe(Guid.Empty);
}
