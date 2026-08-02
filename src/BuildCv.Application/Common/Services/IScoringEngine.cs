namespace BuildCv.Application.Common.Services;

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
}
