using BuildCv.Application.Common.Services;
using BuildCv.Application.Resumes;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Resumes;

// The import side of the signed pass-through: what POST /resumes/import does with the token the propose
// step handed the review screen.
//
// Three refusals and one acceptance, and the refusals are the point. A token nobody can forge is only
// worth having if a forged one is actually refused — otherwise the signature is a comment.
public class ImportEvidenceTests
{
    private readonly FakeResumeRepository _resumes = new();
    private readonly FakeCandidateProfileRepository _profiles = new();
    private readonly FakeImportEvidenceProtector _evidence = new();
    private readonly CreateResumeFromDraftHandler _handler;
    private readonly AccountId _owner = AccountId.New();

    public ImportEvidenceTests() =>
        _handler = new CreateResumeFromDraftHandler(_resumes, _profiles, _evidence);

    private static readonly ImportSignals Signals =
        ImportSignals.Create(ColumnLayout.Multiple, hadTextLayer: true, pageCount: 2);

    private static ResumeDraft ValidDraft() =>
        new(Contact: new ContactDraft(FullName: "Jane Candidate", Email: "jane@example.com"));

    private Task<ResumeImportResult> Import(string? evidence, ResumeDraft? draft = null) =>
        _handler.Handle(new CreateResumeFromDraftCommand(_owner, draft ?? ValidDraft(), evidence));

    [Fact]
    public async Task Import_WithAValidToken_PersistsTheSignalsOnTheResume()
    {
        var result = await Import(_evidence.Protect(Signals, _owner));

        result.IsSuccess.Should().BeTrue();
        result.Resume!.ImportSignals.Should().Be(Signals);
        _resumes.WriteCount.Should().Be(1);
        _profiles.WriteCount.Should().Be(1, "a successful import feeds the profile as well as the resume");
    }

    // THE ORDINARY CASE. A draft typed by hand carries no token, and that is not an error: the
    // readability engine renormalizes the ATS section out for it.
    [Fact]
    public async Task Import_WithNoToken_SucceedsWithNoSignals()
    {
        var result = await Import(evidence: null);

        result.IsSuccess.Should().BeTrue();
        result.Resume!.ImportSignals.Should().BeNull();
        _evidence.UnprotectCallCount.Should().Be(0, "there is nothing to verify");
        _resumes.WriteCount.Should().Be(1);
        _profiles.WriteCount.Should().Be(1, "a successful import feeds the profile as well as the resume");
    }

    // A blank string is the same as absent rather than a rejection: a client that sends "" for an
    // omitted optional field is not asserting anything, and answering 400 to it would be a contract that
    // depended on how a form serializer treats empty values.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Import_WithABlankToken_IsTreatedAsAbsent(string evidence)
    {
        var result = await Import(evidence);

        result.IsSuccess.Should().BeTrue();
        result.Resume!.ImportSignals.Should().BeNull();
        _evidence.UnprotectCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Import_WithAForgedToken_IsRejectedAndWritesNothing()
    {
        var result = await Import(FakeImportEvidenceProtector.ForgedToken);

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Path = CreateResumeFromDraftHandler.ImportEvidencePath,
                Message = IImportEvidenceProtector.InvalidTokenError,
            });
        _resumes.WriteCount.Should().Be(0, "a rejected import creates nothing at all");
        _profiles.WriteCount.Should().Be(0, "a rejected import creates nothing at all");
    }

    // The account binding, and it is asserted against a token that is otherwise perfectly valid — issued
    // by this same protector, unexpired, correctly signed. The ONLY difference from the accepted case is
    // whose it is.
    [Fact]
    public async Task Import_WithAnotherAccountsToken_IsRejected()
    {
        var somebodyElse = AccountId.New();

        var result = await Import(_evidence.Protect(Signals, somebodyElse));

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle()
            .Which.Message.Should().Be(IImportEvidenceProtector.InvalidTokenError);
        _resumes.WriteCount.Should().Be(0);
        _profiles.WriteCount.Should().Be(0, "a rejected import feeds neither store");
    }

    // Expiry gets its own message because it names a different fix and gives nothing away: a caller
    // holding an expired token of their own already knew it was valid.
    [Fact]
    public async Task Import_WithAnExpiredToken_IsRejectedAndSaysSo()
    {
        var token = _evidence.Protect(Signals, _owner);
        _evidence.Expire(token);

        var result = await Import(token);

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle()
            .Which.Message.Should().Be(IImportEvidenceProtector.ExpiredTokenError);
        _resumes.WriteCount.Should().Be(0);
        _profiles.WriteCount.Should().Be(0, "a rejected import feeds neither store");
    }

    // COLLECTED, NOT SHORT-CIRCUITED. A candidate whose token expired while they were correcting their
    // CV has to be told about the token AND the field that is also wrong, in one answer — the whole
    // reason this use case reports per-field errors instead of a single string.
    [Fact]
    public async Task Import_WithABadTokenAndABadField_ReportsBoth()
    {
        var draft = new ResumeDraft(
            Contact: new ContactDraft(FullName: "Jane Candidate", Email: "not-an-email"));

        var result = await Import(FakeImportEvidenceProtector.ForgedToken, draft);

        result.FieldErrors.Select(error => error.Path).Should().BeEquivalentTo(
            ["contact.email", CreateResumeFromDraftHandler.ImportEvidencePath]);
        _resumes.WriteCount.Should().Be(0);
        _profiles.WriteCount.Should().Be(0, "a rejected import feeds neither store");
    }

    // A draft that fails on its own fields must not have its token verified away silently either: the
    // signals are only ever attached to a resume that was actually created.
    [Fact]
    public async Task Import_WithAValidTokenAndABadField_CreatesNothing()
    {
        var draft = new ResumeDraft(Contact: new ContactDraft(FullName: null, Email: "jane@example.com"));

        var result = await Import(_evidence.Protect(Signals, _owner), draft);

        result.IsSuccess.Should().BeFalse();
        result.Resume.Should().BeNull();
        _resumes.WriteCount.Should().Be(0);
        _profiles.WriteCount.Should().Be(0, "a rejected import feeds neither store");
    }
}
