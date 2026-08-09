namespace BuildCv.Application.Readability;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Readability;

public sealed record GetReadabilityReportByIdQuery(AccountId RequesterId, ReadabilityReportId ReportId)
    : IQuery<Result<ReadabilityReport>>;

// Reading back one readability run. Two reads, never one: a ReadabilityReport names a ResumeId and no
// owner, so the only account that may read it is the one that owns the resume it was evaluated from.
// This is GetAnalysisByIdHandler's shape applied to the second aggregate, deliberately including the
// parts that look redundant — the argument for each is below.
public sealed class GetReadabilityReportByIdHandler(
    IReadabilityReportRepository readabilityReportRepository,
    IResumeRepository resumeRepository)
    : IQueryHandler<GetReadabilityReportByIdQuery, Result<ReadabilityReport>>
{
    public async Task<Result<ReadabilityReport>> Handle(
        GetReadabilityReportByIdQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await readabilityReportRepository.GetByIdAsync(query.ReportId, cancellationToken);
            if (report is null)
                return Result<ReadabilityReport>.Failure("Readability report not found.");

            var resume = await resumeRepository.GetByIdAsync(report.ResumeId, cancellationToken);

            // "Readability report not found.", NOT "Resume not found." — the truthful answer to the route
            // the caller used, which named a report. A report whose resume is gone is not readable:
            // deleting a resume is a promise that everything derived from it disappears with it, and both
            // providers now keep it (ResumeRepository.CascadeToReadabilityReportsAsync tombstones,
            // InMemoryResumeRepository.DeleteAsync drops). Naming the resume instead would report on a
            // resource the caller did not ask about and does not own.
            //
            // Unreachable under EF, where the cascade means an orphan never survives the query filter and
            // the miss happens above. The in-memory store has no cascade to RUN — it removes the row
            // outright — so it misses above as well. The branch is kept anyway for the same reason the
            // scoring one is: it is the right answer for a row that is genuinely orphaned, and it is what
            // keeps the two providers agreeing on the MESSAGE rather than only on the status.
            if (resume is null)
                return Result<ReadabilityReport>.Failure("Readability report not found.");

            // Owner only, matching EvaluateResumeReadabilityHandler, which is the endpoint that wrote
            // this row. The advice quotes the candidate's own bullet points and job titles back at them —
            // ReadabilityRecommendation.Message is encrypted at rest under its own context string for
            // exactly that reason — so this is not the aggregate to widen to Role.Admin by reflex.
            if (resume.OwnerId != query.RequesterId)
                return Result<ReadabilityReport>.Failure("Forbidden.");

            // The report is returned as it was taken. There is no staleness flag here, unlike
            // AnalysisView: the resume was loaded to AUTHORIZE, and readability has no counterpart to
            // Analysis.ResumeUpdatedAt to compare it against — nothing on ReadabilityReport records the
            // state of the CV it graded. Deriving one from EvaluatedAt against Resume.UpdatedAt would be
            // a different predicate wearing the same name, so it is left out rather than approximated.
            return Result<ReadabilityReport>.Success(report);
        }
        catch (DomainException ex)
        {
            return Result<ReadabilityReport>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<ReadabilityReport>.Failure(ex.Message);
        }
    }
}
