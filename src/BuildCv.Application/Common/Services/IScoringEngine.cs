namespace BuildCv.Application.Common.Services;

using BuildCv.Application.Scoring;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

public interface IScoringEngine
{
    // ScoreResult rather than ScoreBreakdown: the numbers and the advice come out of ONE pass, because
    // the advice is derived from the numbers. Returning the breakdown and expecting the caller to ask
    // a second component for recommendations would let the two be computed against different inputs,
    // and a recommendation that describes a score nobody was shown is worse than none.
    ScoreResult Score(Resume resume, JobPosting jobPosting, DateOnly referenceDate);

    // Separate from Score, and it has to be, because the caller needs it on a path where Score never runs:
    // ScoreResume reuses a stored analysis when the resume and the posting have not moved, and that reuse
    // must still be able to answer WHAT MATCHED. Folding this into ScoreResult would make attribution
    // available exactly on the requests that recomputed and absent on the ones that did not — the same
    // question answered two ways depending on a cache hit.
    //
    // Safe on the de-duplicated path precisely because the key demands ResumeUpdatedAt equality: a reuse
    // is proof the resume has not changed, so attributing against it now describes the snapshot that was
    // scored. Attribution is never persisted for the opposite reason — a STORED analysis, read later, has
    // no such proof.
    IReadOnlyList<RequirementAttribution> Attribute(Resume resume, JobPosting jobPosting);
}
