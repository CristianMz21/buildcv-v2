namespace BuildCv.Application.Common.Services;

using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;

public interface IReadabilityEngine
{
    // ONE RESUME AND A DATE. No JobPosting parameter, and that absence is the feature rather than an
    // omission: readability is a fact about the CV alone, so a candidate gets an answer before any
    // posting exists in the system. Adding a posting parameter here would make the answer depend on
    // something the caller may not have, which is exactly what Analysis already covers.
    //
    // The date is taken rather than read from a clock, for the reason ScoreResumeHandler snapshots one
    // instant: an employment gap is measured against "today", so a run that read the clock twice could
    // straddle midnight and report a timeline it was not evaluated against.
    //
    // ReadabilityResult rather than ReadabilityBreakdown: the numbers and the advice come out of ONE
    // pass, because the advice is derived from the numbers. Returning the breakdown and expecting the
    // caller to ask a second component for recommendations would let the two be computed against
    // different inputs, and a recommendation that describes a score nobody was shown is worse than none.
    ReadabilityResult Evaluate(Resume resume, DateOnly referenceDate);
}
