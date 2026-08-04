namespace BuildCv.Application.Jobs;

using BuildCv.Application.Common;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;

/// <summary>
/// Creates a candidate-owned Draft <see cref="JobPosting"/> from one reviewed <see cref="JobOfferDraft"/>
/// — the "bring your own job offer" path, so a candidate can score against the opportunity they are
/// actually chasing without needing the recruiter authoring surface.
/// </summary>
/// <remarks>
/// A use case of ITS OWN, deliberately not a widening of <c>CreateJobPostingHandler</c>. That handler's
/// <c>CanPostJobs</c> gate is what keeps candidates off the recruiter path; loosening it to serve a
/// different need is how one use case quietly becomes two. The posting it creates is a <c>Draft</c>
/// owned by the candidate — <see cref="ScoreResumeHandler"/> already admits an owner's own Draft, so it
/// is scorable by its owner and by nobody else until (and unless) it is published, which
/// <see cref="PublishJobPostingHandler"/> now refuses to a candidate.
/// </remarks>
public sealed record ImportJobOfferCommand(AccountId RequesterId, JobOfferDraft Draft)
    : ICommand<JobOfferImportResult>;

// One AddAsync, like CreateResumeFromDraft: the whole posting is assembled in memory and handed to the
// repository once. No try/catch here, because JobOfferDraftValidator has already made every Domain call
// this use case makes and turned each throw into a FieldError; a catch around Validate could only catch
// a bug, and dressing a bug up as a 400 the client can "fix" is worse than the 500 it deserves. AddAsync
// is left uncaught on purpose -- a lost connection or a deadlock is a 500, correctly.
public sealed class ImportJobOfferHandler(IJobPostingRepository jobPostingRepository)
    : ICommandHandler<ImportJobOfferCommand, JobOfferImportResult>
{
    public async Task<JobOfferImportResult> Handle(
        ImportJobOfferCommand command, CancellationToken cancellationToken = default)
    {
        var result = JobOfferDraftValidator.Validate(command.RequesterId, command.Draft);
        if (!result.IsSuccess)
            return result;

        await jobPostingRepository.AddAsync(result.JobPosting!, cancellationToken);
        return result;
    }
}

/// <summary>
/// Either the Draft posting that was created, or every field of the draft that stopped it — never both,
/// and never neither.
/// </summary>
/// <remarks>
/// A per-use-case result type, the same shape and for the same reason as
/// <c>ResumeImportResult</c>: <see cref="BuildCv.Domain.Common.ValueObjects.Result{T}"/> carries one
/// string and the API routes on it, while a draft rejects field by field. There is no third "forbidden"
/// case because the posting is created FOR the requester — nothing to look up, own, or be forbidden from.
/// </remarks>
public sealed record JobOfferImportResult
{
    public JobPosting? JobPosting { get; }
    public IReadOnlyList<FieldError> FieldErrors { get; }
    public bool IsSuccess => JobPosting is not null;

    private JobOfferImportResult(JobPosting? jobPosting, IReadOnlyList<FieldError> fieldErrors)
    {
        JobPosting = jobPosting;
        FieldErrors = fieldErrors;
    }

    public static JobOfferImportResult Imported(JobPosting jobPosting)
    {
        ArgumentNullException.ThrowIfNull(jobPosting);
        return new JobOfferImportResult(jobPosting, []);
    }

    // The empty check is what makes "no errors means valid" a property of the TYPE: a rejection carrying
    // an empty list would read as a success with no posting at every call site that tests FieldErrors.
    public static JobOfferImportResult Rejected(IReadOnlyList<FieldError> fieldErrors)
    {
        ArgumentNullException.ThrowIfNull(fieldErrors);
        if (fieldErrors.Count == 0)
            throw new ArgumentException("A rejected draft must carry at least one field error.", nameof(fieldErrors));

        return new JobOfferImportResult(null, fieldErrors);
    }
}
