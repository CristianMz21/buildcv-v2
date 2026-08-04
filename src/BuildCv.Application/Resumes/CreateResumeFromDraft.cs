namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

/// <summary>
/// Creates a complete resume from one reviewed draft, in place of the sixteen round trips
/// <c>POST /resumes</c> plus the per-section routes demand.
/// </summary>
public sealed record CreateResumeFromDraftCommand(AccountId RequesterId, ResumeDraft Draft)
    : ICommand<ResumeImportResult>;

// The whole aggregate is assembled in memory and handed to the repository in a SINGLE AddAsync. That
// is not a micro-optimization: ten Add-then-Update calls would each be a write, so a draft that failed
// halfway would leave a resume the candidate cannot tell the shape of, and re-importing would
// duplicate whatever landed.
//
// No new port, no repository change and no new configuration is needed, and that is checked rather than
// assumed: ResumeRepository.AddAsync is `_context.Resumes.Add(resume)` plus one SaveChanges, and
// SchemaRoundTripTests.Resume_RoundTrips_WithEveryChildCollectionAndFullContactInformation already
// proves against a real SQL Server that exactly that call persists all ten owned collections, the
// Website and the Profiles in one go.
public sealed class CreateResumeFromDraftHandler(IResumeRepository resumeRepository)
    : ICommandHandler<CreateResumeFromDraftCommand, ResumeImportResult>
{
    public async Task<ResumeImportResult> Handle(
        CreateResumeFromDraftCommand command, CancellationToken cancellationToken = default)
    {
        // No try/catch here, unlike every sibling handler in this folder, because ResumeDraftValidator
        // has already made every DOMAIN call this use case makes and turned each DomainException and
        // ArgumentException into a FieldError. A catch around Validate could only catch a bug, and
        // dressing a bug up as a 400 the client can "fix" is worse than the 500 it deserves.
        //
        // AddAsync is a different matter and is deliberately left uncaught. It runs after the validator
        // and can fail on things no amount of validation can inspect — a lost connection, a deadlock, a
        // constraint the Domain does not model. Those are 500s, correctly. The one case that was NOT
        // legitimate is now closed at its source: a language name longer than its nvarchar(100) column
        // used to arrive here as SQL Server error 2628, untranslated, so Language.Create owns that
        // length rule and the validator catches it like any other. If another bounded plaintext column
        // is ever added, its rule belongs on the Domain type too, not in a catch here.
        var result = ResumeDraftValidator.Validate(command.RequesterId, command.Draft);
        if (!result.IsSuccess)
            return result;

        await resumeRepository.AddAsync(result.Resume!, cancellationToken);
        return result;
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
