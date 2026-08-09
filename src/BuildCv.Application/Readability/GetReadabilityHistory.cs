namespace BuildCv.Application.Readability;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;

// Limit and Cursor arrive RAW, exactly as the query string carried them, matching
// GetAnalysisHistoryQuery: a malformed cursor comes back as an ordinary Result failure rather than as a
// second, hand-rolled error path in the Api layer.
public sealed record GetReadabilityHistoryQuery(
    AccountId RequesterId,
    ResumeId ResumeId,
    int? Limit = null,
    string? Cursor = null)
    : IQuery<Result<Page<ReadabilityReport>>>;

// Every readability run this resume has ever been given, OLDEST FIRST. That direction is argued in full
// on IReadabilityReportRepository.GetPageByResumeIdAsync and lives in its three implementations; this
// handler only has to not re-sort them.
public sealed class GetReadabilityHistoryHandler(
    IResumeRepository resumeRepository,
    IReadabilityReportRepository readabilityReportRepository)
    : IQueryHandler<GetReadabilityHistoryQuery, Result<Page<ReadabilityReport>>>
{
    public async Task<Result<Page<ReadabilityReport>>> Handle(
        GetReadabilityHistoryQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            // Authorization first, before the cursor is so much as looked at — the order
            // GetAnalysisHistoryHandler already establishes. A caller with no business reading this
            // resume gets the same 403 whatever else it sent, so the difference between "forbidden" and
            // "malformed" never becomes a probe for whether somebody else's resume exists, and nothing
            // queries the readability history of a resume the caller does not own.
            var resume = await resumeRepository.GetByIdAsync(query.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Page<ReadabilityReport>>.Failure("Resume not found.");

            if (resume.OwnerId != query.RequesterId)
                return Result<Page<ReadabilityReport>>.Failure("Forbidden.");

            var page = PageRequest.Create(query.Limit, query.Cursor);
            if (!page.IsSuccess)
                return Result<Page<ReadabilityReport>>.Failure(page.Error!);

            // Returned exactly as the store handed it over. Nothing here may re-shape a page, because
            // Page<T>.From is the only copy of the boundary arithmetic, and the cursor it carries is the
            // position of the last row actually delivered.
            var history = await readabilityReportRepository.GetPageByResumeIdAsync(
                query.ResumeId, page.Value!, cancellationToken);

            return Result<Page<ReadabilityReport>>.Success(history);
        }
        catch (DomainException ex)
        {
            return Result<Page<ReadabilityReport>>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<Page<ReadabilityReport>>.Failure(ex.Message);
        }
    }
}
