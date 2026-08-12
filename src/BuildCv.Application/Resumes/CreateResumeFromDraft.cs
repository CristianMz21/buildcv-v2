namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

/// <summary>
/// Creates a complete resume from one reviewed draft, in place of the sixteen round trips
/// <c>POST /resumes</c> plus the per-section routes demand.
/// </summary>
/// <param name="ImportEvidence">
/// The opaque token <c>POST /resumes/import/propose</c> minted for the document this draft was extracted
/// from, or null for a draft typed by hand. It is NOT a field of <see cref="ResumeDraft"/> and must not
/// become one: that type is "the resume as a review screen holds it", every leaf a nullable string the
/// candidate may correct, and this is a credential the candidate must NOT correct — a hand-edited token
/// is a rejected one.
/// </param>
public sealed record CreateResumeFromDraftCommand(
    AccountId RequesterId,
    ResumeDraft Draft,
    string? ImportEvidence = null) : ICommand<ResumeImportResult>;

// The import is TWO WRITES into two aggregates, and the ordering is the property rather than a
// preference. The candidate's master data lands in their PROFILE first, then the resume itself —
// because one of those writes is idempotent and the other is not, and that is what makes a retry safe.
// The profile write is create-or-merge: every Add* on CandidateProfile ignores what the profile already
// holds, so re-importing the same document, or a corrected one, merges into the same profile and
// nothing duplicates. The resume write is the one that MUST NOT run twice — each AddAsync inserts a
// fresh resume — so it runs last, only once the merge has succeeded: a failure between the two loses
// nothing, the retry merges again (a no-op) and recreates the resume against a profile that already
// holds the data; the reverse order would recreate the resume against a profile that lost it.
//
// The resume itself is still assembled in memory and handed to its repository in a SINGLE AddAsync,
// and that half is not a micro-optimization either: ten Add-then-Update calls would each be a write,
// so a draft that failed halfway would leave a resume the candidate cannot tell the shape of. It is
// verified to persist all ten owned collections, the Website and the Profiles in one go against a real
// SQL Server by
// SchemaRoundTripTests.Resume_RoundTrips_WithEveryChildCollectionAndFullContactInformation.
public sealed class CreateResumeFromDraftHandler(
    IResumeRepository resumeRepository,
    ICandidateProfileRepository candidateProfileRepository,
    IImportEvidenceProtector importEvidenceProtector)
    : ICommandHandler<CreateResumeFromDraftCommand, ResumeImportResult>
{
    /// <summary>
    /// The JSON path a rejected token is reported at. It is a top-level field of the request body, beside
    /// the draft's own sections rather than inside them, so the review screen highlights the upload it
    /// came from and not one of the forty fields the candidate typed.
    /// </summary>
    public const string ImportEvidencePath = "importEvidence";

    public async Task<ResumeImportResult> Handle(
        CreateResumeFromDraftCommand command, CancellationToken cancellationToken = default)
    {
        // No try/catch here, unlike every sibling handler in this folder, because ResumeDraftValidator
        // has already made every DOMAIN call this use case makes and turned each DomainException and
        // ArgumentException into a FieldError. A catch around Validate could only catch a bug, and
        // dressing a bug up as a 400 the client can "fix" is worse than the 500 it deserves.
        //
        // The persistence writes — the profile's and the resume's — are deliberately left uncaught.
        // They run after the validator and can fail on things no amount of validation can inspect — a
        // lost connection, a deadlock, a constraint the Domain does not model. Those are 500s,
        // correctly. The one case that was NOT legitimate is now closed at its source: a language name
        // longer than its nvarchar(100) column used to arrive here as SQL Server error 2628,
        // untranslated, so Language.Create owns that length rule and the validator catches it like any
        // other. If another bounded plaintext column is ever added, its rule belongs on the Domain type
        // too, not in a catch here.
        // Verified BEFORE the draft is validated, so the signals can be built into the aggregate in the
        // same single construction everything else goes through — there is no "create then attach", and
        // therefore no window in which a resume exists without the evidence it was imported with.
        var evidence = VerifyEvidence(command);

        var result = ResumeDraftValidator.Validate(command.RequesterId, command.Draft, evidence.Signals);

        // COLLECTED, not short-circuited, in both directions. A candidate whose token expired while they
        // were correcting their CV must be told about the token AND about the three fields that are also
        // wrong, in one response — the whole reason this use case reports per-field errors instead of
        // returning Result<T>. Rejecting on the token alone would send them back to fix one thing and
        // meet the next three on the following attempt.
        if (evidence.Error is not null)
            return ResumeImportResult.Rejected([.. result.FieldErrors, evidence.Error]);

        if (!result.IsSuccess)
            return result;

        await WriteProfileAsync(command.RequesterId, result.Resume!, cancellationToken);
        await resumeRepository.AddAsync(result.Resume!, cancellationToken);
        return result;
    }

    // THE PROFILE IS WRITTEN BEFORE THE RESUME, and the two writes are deliberately not one unit of
    // work: they go to two repositories and there is no transaction across them. What keeps a retry
    // honest is the ORDER, not atomicity — see the remarks on the class for the full argument. The
    // one extra property this method owns is that the merge is by VALUE rather than by instance.
    private async Task WriteProfileAsync(
        AccountId ownerId, Resume resume, CancellationToken cancellationToken)
    {
        // The clone is an EF requirement rather than a style choice: the two aggregates are written to
        // the same DbContext in one request, and an OwnsMany entry is a row owned by exactly one
        // principal. If the profile shared Resume's own instances, the second SaveChanges would find
        // those rows already tracked under the first owner and silently move them — or re-insert them.
        var contact = resume.ContactInformation with { };

        var profile = await candidateProfileRepository.GetByOwnerIdAsync(ownerId, cancellationToken);
        if (profile is null)
        {
            profile = CandidateProfile.Create(ownerId, contact);
            MergeInto(profile, resume);
            await candidateProfileRepository.AddAsync(profile, cancellationToken);
            return;
        }

        // CONTACT IS MERGED IN THE PROFILE'S DIRECTION, never the other way. The profile is what the
        // candidate typed or corrected by hand; the draft's contact is a convenience source that fills
        // only what the profile does not already have. An import that replaced the profile's contact
        // wholesale would silently destroy a hand-typed correction the moment an old draft was
        // re-imported — the exact data-loss this aggregate exists to prevent.
        profile.UpdateContactInformation(ContactInformation.GapFill(profile.ContactInformation, contact));
        MergeInto(profile, resume);
        await candidateProfileRepository.UpdateAsync(profile, cancellationToken);
    }

    // Every Add on a CandidateProfile is idempotent, so this is a merge rather than an append and
    // crashing on a duplicate is impossible: re-importing the same document, or a corrected one, is the
    // ordinary case. The item types are the same records Resume holds — a shared definition by design —
    // so the copy is these ten loops and not a translation. Each entry is copied by `with { }` for the
    // ownership reason above; the nested collections are converter columns rather than owned rows, so
    // sharing their list instances is harmless and one level of copy is exactly enough.
    private static void MergeInto(CandidateProfile profile, Resume resume)
    {
        foreach (var experience in resume.Experiences) profile.AddExperience(experience with { });
        foreach (var education in resume.Educations) profile.AddEducation(education with { });
        foreach (var skill in resume.Skills) profile.AddSkill(skill with { });
        foreach (var project in resume.Projects) profile.AddProject(project with { });
        foreach (var certificate in resume.Certificates) profile.AddCertificate(certificate with { });
        foreach (var language in resume.Languages) profile.AddLanguage(language with { });
        foreach (var award in resume.Awards) profile.AddAward(award with { });
        foreach (var publication in resume.Publications) profile.AddPublication(publication with { });
        foreach (var interest in resume.Interests) profile.AddInterest(interest with { });
        foreach (var reference in resume.References) profile.AddReference(reference with { });
    }

    // Absent evidence is the ordinary case and is not an error: a hand-typed draft has no document to
    // describe, and the readability engine renormalizes the ATS-parseability section out for it.
    //
    // An evidence string that IS present and does not verify is refused rather than dropped. Dropping it
    // would answer 201 to a request the server could not honour, and the candidate would then be shown a
    // readability report missing a section they supplied evidence for, with nothing anywhere saying why.
    private (ImportSignals? Signals, FieldError? Error) VerifyEvidence(CreateResumeFromDraftCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ImportEvidence))
            return (null, null);

        var unprotected = importEvidenceProtector.Unprotect(command.ImportEvidence, command.RequesterId);
        return unprotected.IsSuccess
            ? (unprotected.Value, null)
            : (null, new FieldError(ImportEvidencePath, unprotected.Error!));
    }
}

/// <summary>
/// Either the resume that was created, or every field of the draft that stopped it — never both, and
/// never neither.
/// </summary>
/// <remarks>
/// A second failure channel is NOT bolted onto <see cref="BuildCv.Domain.Common.ValueObjects.Result{T}"/>:
/// that type is the return of every other handler in the repository and <c>ToHttpResult</c> routes on
/// the single string it carries. Per-field failure is this one use case's need, so it is this one use
/// case's type.
/// <para>
/// There is no third "something else went wrong" case because this path has no such failure: the
/// resume is created for the requester, so there is nothing to look up, nothing to own and nothing to
/// be forbidden from. If a later change adds one, it belongs in <c>Result{T}</c>'s existing
/// message-routed shape, not in <c>FieldErrors</c>.
/// </para>
/// </remarks>
public sealed record ResumeImportResult
{
    public Resume? Resume { get; }
    public IReadOnlyList<FieldError> FieldErrors { get; }
    public bool IsSuccess => Resume is not null;

    private ResumeImportResult(Resume? resume, IReadOnlyList<FieldError> fieldErrors)
    {
        Resume = resume;
        FieldErrors = fieldErrors;
    }

    public static ResumeImportResult Imported(Resume resume)
    {
        ArgumentNullException.ThrowIfNull(resume);
        return new ResumeImportResult(resume, []);
    }

    // The empty check is what makes "no errors means valid" a property of the TYPE rather than a
    // convention every caller has to remember: a rejection carrying an empty list would read as a
    // success with no resume at every call site that tests FieldErrors.Count.
    public static ResumeImportResult Rejected(IReadOnlyList<FieldError> fieldErrors)
    {
        ArgumentNullException.ThrowIfNull(fieldErrors);
        if (fieldErrors.Count == 0)
            throw new ArgumentException("A rejected draft must carry at least one field error.", nameof(fieldErrors));

        return new ResumeImportResult(null, fieldErrors);
    }
}
