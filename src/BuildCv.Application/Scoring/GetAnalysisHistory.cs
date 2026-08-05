namespace BuildCv.Application.Scoring;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

// Limit and Cursor arrive RAW, exactly as the query string carried them, matching
// GetResumesByOwnerQuery: a malformed cursor comes back as an ordinary Result failure rather than as a
// second, hand-rolled error path in the Api layer.
public sealed record GetAnalysisHistoryQuery(
    AccountId RequesterId,
    ResumeId ResumeId,
    int? Limit = null,
    string? Cursor = null)
    : IQuery<Result<Page<AnalysisView>>>;

// Every score this resume has ever been given, OLDEST FIRST. That direction is the single exception to
// this repo's newest-first convention and it is not a detail: a history is read forwards — the first run,
// then what each edit changed — which is the whole feedback loop the product is. The direction lives in
// IAnalysisRepository and its three implementations; this handler only has to not re-sort them.
public sealed class GetAnalysisHistoryHandler(
    IResumeRepository resumeRepository,
    IAnalysisRepository analysisRepository)
    : IQueryHandler<GetAnalysisHistoryQuery, Result<Page<AnalysisView>>>
{
    public async Task<Result<Page<AnalysisView>>> Handle(
        GetAnalysisHistoryQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            // Authorization first, before the cursor is so much as looked at — the order
            // GetResumesByOwnerHandler already establishes. A caller with no business reading this
            // resume gets the same 403 whatever else it sent, so the difference between "forbidden" and
            // "malformed" never becomes a probe, and nothing queries the analyses of a resume the
            // caller does not own.
            var resume = await resumeRepository.GetByIdAsync(query.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Page<AnalysisView>>.Failure("Resume not found.");

            if (resume.OwnerId != query.RequesterId)
                return Result<Page<AnalysisView>>.Failure("Forbidden.");

            var page = PageRequest.Create(query.Limit, query.Cursor);
            if (!page.IsSuccess)
                return Result<Page<AnalysisView>>.Failure(page.Error!);

            var history = await analysisRepository.GetPageByResumeIdAsync(
                query.ResumeId, page.Value!, cancellationToken);

            // Every entry is compared against the SAME resume — the one just loaded — so a history page
            // reads as a single sweep: the older entries are stale, and the run that matches the CV as it
            // stands now is not. The page's own cursor is carried through untouched; nothing here may
            // re-shape a page, because Page<T>.From is the only copy of that arithmetic.
            return Result<Page<AnalysisView>>.Success(new Page<AnalysisView>(
                [.. history.Items.Select(analysis => AnalysisView.Of(analysis, resume))],
                history.NextCursor));
        }
        catch (DomainException ex)
        {
            return Result<Page<AnalysisView>>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<Page<AnalysisView>>.Failure(ex.Message);
        }
    }
}
