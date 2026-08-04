using BuildCv.Application.Common;
using BuildCv.Application.Jobs;
using BuildCv.Application.Tests.Common.Pagination;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using FluentAssertions;

namespace BuildCv.Application.Tests.Jobs;

// POST /job-offers/import at the Application layer. It reuses the shared FieldErrorCollector mechanism,
// so the field-error, null-element, cap and duplicate behaviours are the resume importer's behaviours
// re-exercised through the job walk -- pinned here so a divergence in the shared helper is caught on
// both sides.
public class ImportJobOfferHandlerTests
{
    private readonly FakeJobPostingRepository _jobPostings = new();
    private readonly ImportJobOfferHandler _handler;

    public ImportJobOfferHandlerTests() => _handler = new ImportJobOfferHandler(_jobPostings);

    private static JobOfferDraft Draft(params JobRequirementDraft[] requirements) =>
        new("Senior Backend Engineer", "Contoso", requirements);

    private async Task<JobOfferImportResult> ImportAsync(AccountId owner, JobOfferDraft draft) =>
        await _handler.Handle(new ImportJobOfferCommand(owner, draft));

    [Fact]
    public async Task Import_AValidOffer_CreatesADraftPostingOwnedByTheCandidate()
    {
        var owner = AccountId.New();

        var result = await ImportAsync(owner, Draft(
            new JobRequirementDraft("C#", "MustHave"),
            new JobRequirementDraft("Docker", "NiceToHave")));

        result.IsSuccess.Should().BeTrue();
        var posting = result.JobPosting!;
        posting.OwnerId.Should().Be(owner);
        posting.Status.Should().Be(JobPostingStatus.Draft);
        posting.Title.Should().Be("Senior Backend Engineer");
        posting.CompanyName!.Value.Should().Be("Contoso");
        posting.Requirements.Select(r => r.Skill.Name).Should().Equal("C#", "Docker");

        var stored = await _jobPostings.GetByIdAsync(posting.Id);
        stored.Should().NotBeNull("the created offer is persisted in one write");
    }

    // A blank priority defaults to NiceToHave -- the conservative rung, never the Critical-driving
    // MustHave. Goes red if the default is flipped or dropped.
    [Fact]
    public async Task Import_ABlankPriority_DefaultsToNiceToHave()
    {
        var result = await ImportAsync(AccountId.New(), Draft(new JobRequirementDraft("C#", Priority: null)));

        result.IsSuccess.Should().BeTrue();
        result.JobPosting!.Requirements.Single().Priority.Should().Be(RequirementPriority.NiceToHave);
    }

    // Weight is NEVER carried on the draft; it derives from Priority in JobRequirement.Create. Both rungs
    // are checked so a change that let a parser set Weight, or that broke the derivation, goes red.
    [Theory]
    [InlineData("MustHave", 1.0)]
    [InlineData("NiceToHave", 0.5)]
    public async Task Import_Weight_AlwaysDerivesFromPriority(string priority, double expectedWeight)
    {
        var result = await ImportAsync(AccountId.New(), Draft(new JobRequirementDraft("C#", priority)));

        result.IsSuccess.Should().BeTrue();
        result.JobPosting!.Requirements.Single().Weight.Should().Be(expectedWeight);
    }

    [Fact]
    public async Task Import_AnUnknownPriority_IsAFieldErrorAndCreatesNothing()
    {
        var owner = AccountId.New();

        var result = await ImportAsync(owner, Draft(new JobRequirementDraft("C#", "Critical")));

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle()
            .Which.Should().Be(new FieldError("requirements[0].priority", "Invalid requirement priority."));
        await AssertNothingCreatedFor(owner);
    }

    [Fact]
    public async Task Import_ABlankTitleAndCompany_ReportsBothFields()
    {
        var result = await _handler.Handle(new ImportJobOfferCommand(
            AccountId.New(), new JobOfferDraft(Title: null, CompanyName: " ", Requirements: null)));

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Select(e => e.Path).Should().BeEquivalentTo("title", "companyName");
    }

    // The duplicate guard lives on JobPosting.AddRequirement and is case-insensitive on the skill name.
    // The walk is in draft order, so it is the LATER occurrence that is reported -- the line to delete.
    [Fact]
    public async Task Import_ADuplicateSkill_ReportsTheLaterOccurrence()
    {
        var owner = AccountId.New();

        var result = await ImportAsync(owner, Draft(
            new JobRequirementDraft("React", "NiceToHave"),
            new JobRequirementDraft("react", "MustHave")));

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle().Which.Path.Should().Be("requirements[1].skill");
        await AssertNothingCreatedFor(owner);
    }

    [Fact]
    public async Task Import_ABlankSkill_IsAFieldError()
    {
        var result = await ImportAsync(AccountId.New(), Draft(new JobRequirementDraft(Skill: " ", Priority: "MustHave")));

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle()
            .Which.Should().Be(new FieldError("requirements[0].skill", "Value is required."));
    }

    // A null element in the requirements array is a field error at its own index, not a crash -- the
    // shared ForEachCapped handling, re-exercised on the job walk.
    [Fact]
    public async Task Import_ANullRequirementElement_IsAFieldErrorAtThatIndex()
    {
        var result = await _handler.Handle(new ImportJobOfferCommand(
            AccountId.New(), new JobOfferDraft("Role", "Contoso", [null])));

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle()
            .Which.Should().Be(new FieldError("requirements[0]", "Value is required."));
    }

    [Fact]
    public async Task Import_OverTheRequirementsCap_IsRefusedAndCreatesNothing()
    {
        var owner = AccountId.New();
        var tooMany = Enumerable.Range(0, 101)
            .Select(i => new JobRequirementDraft($"Skill{i}", "NiceToHave"))
            .ToArray();

        var result = await ImportAsync(owner, Draft(tooMany));

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle()
            .Which.Should().Be(new FieldError("requirements", "Too many items. At most 100 are accepted."));
        await AssertNothingCreatedFor(owner);
    }

    // EXTRACTION PROPOSES, IT NEVER COMMITS. The extractor proposes NiceToHave; the candidate promotes
    // "C#" to MustHave on the review screen and drops the rest; the created posting is the one the
    // candidate CONFIRMED, not the one the heuristic proposed. Import reads only the submitted draft.
    [Fact]
    public async Task Import_TheConfirmedDraftWins_NotTheExtractorsProposal()
    {
        var proposed = JobRequirementExtractor.Extract("We use C# and PostgreSQL.");
        proposed.Should().Contain(p => p.Skill == "C#" && p.Priority == RequirementPriority.NiceToHave);

        var confirmed = Draft(new JobRequirementDraft("C#", "MustHave"));

        var result = await ImportAsync(AccountId.New(), confirmed);

        result.IsSuccess.Should().BeTrue();
        var requirement = result.JobPosting!.Requirements.Should().ContainSingle().Subject;
        requirement.Skill.Name.Should().Be("C#");
        requirement.Priority.Should().Be(RequirementPriority.MustHave,
            "the candidate promoted it -- the proposal's NiceToHave must not have stuck");
    }

    private async Task AssertNothingCreatedFor(AccountId owner) =>
        (await _jobPostings.GetPageByOwnerIdAsync(owner, PageRequests.Of())).Items
            .Should().BeEmpty("a rejected draft must create no posting");
}
