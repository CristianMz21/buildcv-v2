namespace BuildCv.Application.Common.Repositories;

using BuildCv.Application.Common.Pagination;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;

// Readability reports. Append-only, exactly like IAnalysisRepository: a report is a fact about a moment,
// never edited afterwards, so there is no UpdateAsync to write.
//
// A report has NO OWNER of its own, for the same reason an Analysis has none: it names a resume, and
// that resume's owner is the owner. Authorization is therefore a read in the handler rather than a
// parameter here -- denormalizing an AccountId onto an append-only fact to save that read would give the
// platform two answers to "whose is this" and no way to tell which is stale.
public interface IReadabilityReportRepository
{
    Task AddAsync(ReadabilityReport report, CancellationToken cancellationToken = default);

    // One report by its own id, with no owner filter, for the reason stated above.
    Task<ReadabilityReport?> GetByIdAsync(
        ReadabilityReportId id, CancellationToken cancellationToken = default);

    // OLDEST FIRST -- the same exception IAnalysisRepository.GetPageByResumeIdAsync makes, rather than a
    // second one, and the direction was chosen rather than copied.
    //
    // Three things say a readability history is a history in the sense score history is. It is the same
    // KIND of row: an append-only evaluation event keyed by (ResumeId, Seq), cascaded by
    // ResumeRepository.DeleteAsync alongside the analyses. It hangs off the same resource, at
    // GET /v1/resumes/{id}/readability beside GET /v1/resumes/{id}/analyses, so a client plotting the two
    // trends of one CV would otherwise have to reverse exactly one of them. And the product loop is if
    // anything sharper here than on the scoring side: every ReadabilityRecommendation carries a MEASURED
    // Impact -- the exact rise in the weighted total that acting on that one piece of advice produces --
    // so this list is the record of whether the candidate acted and whether the measurement held, which
    // is read from the first attempt forwards.
    //
    // "The candidate wants the newest one" is the argument for the other direction and it is answered
    // elsewhere: POST /v1/resumes/{id}/readability evaluates the CV as it stands and writes a new row
    // every time -- EvaluateResumeReadabilityHandler has no de-duplication -- so nobody reads page one of
    // this list to find out where they are today. They post.
    //
    // The direction lives here and in all three implementations; the handler above only has to not
    // re-sort them.
    Task<Page<ReadabilityReport>> GetPageByResumeIdAsync(
        ResumeId resumeId, PageRequest page, CancellationToken cancellationToken = default);
}
