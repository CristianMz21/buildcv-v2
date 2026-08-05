namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Services;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

// The real engine with a notebook. It delegates every call, so the scores a test sees are the scores
// production produces -- the only thing added is a record of the referenceDate the engine was HANDED.
//
// That value is otherwise invisible: ScoreResult carries the numbers, never the date they were computed
// against, and Analysis stores ScoredAt rather than the reference date. Without this, "ScoredAt's date
// equals the date actually scored against" can only be checked by re-deriving the expected date from the
// clock, which is the same arithmetic the handler does and would pass whatever the handler did.
public sealed class RecordingScoringEngine(IScoringEngine inner) : IScoringEngine
{
    private readonly List<DateOnly> _referenceDates = [];

    public IReadOnlyList<DateOnly> ReferenceDates => _referenceDates.AsReadOnly();

    public ScoreResult Score(Resume resume, JobPosting jobPosting, DateOnly referenceDate)
    {
        _referenceDates.Add(referenceDate);
        return inner.Score(resume, jobPosting, referenceDate);
    }
}
