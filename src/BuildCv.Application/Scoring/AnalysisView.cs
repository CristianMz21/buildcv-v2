namespace BuildCv.Application.Scoring;

using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

// A stored analysis together with the one thing the row cannot answer about itself: whether the resume
// it scored is still the resume the candidate has. That is the question the product exists to answer
// after the first score — "did my edit help, and is this number even about the CV I have now".
//
// IsStale IS COMPUTED AND NEVER PERSISTED, for exactly the reason Analysis.OverallScore and Band are
// Ignore()d in EF: a stored copy of a derived value becomes a second source of truth. This one would go
// wrong on the very next edit — the resume moves, the analysis row does not, and a persisted flag would
// still read "current" — so there is no invalidation to get right because there is nothing stored.
//
// RESUME-SIDE ONLY, deliberately. Every handler that builds one of these already holds the resume: it is
// how they authorize, since an Analysis has no owner of its own. So this comparison costs no read, while
// the posting side would cost a third one on every score read and nobody has asked for it.
// Analysis.JobPostingUpdatedAt is recorded all the same — the de-duplication key compares it — so adding
// the posting side later is a read, not a migration.
public sealed record AnalysisView(Analysis Analysis, bool IsStale)
{
    // The single place staleness is decided for every endpoint that reports it, so the three cannot
    // drift into three different answers about one row. The rule itself lives on the Domain entity,
    // which is also where the de-duplication key reads it from.
    public static AnalysisView Of(Analysis analysis, Resume resume)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(resume);

        return new AnalysisView(analysis, analysis.IsStaleFor(resume.UpdatedAt));
    }
}
