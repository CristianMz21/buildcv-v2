namespace BuildCv.Application.Scoring;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Scoring;

public sealed record GetAnalysisByIdQuery(AccountId RequesterId, AnalysisId AnalysisId)
    : IQuery<Result<Analysis>>;

// Reading back one score. Two reads, never one: an Analysis names a ResumeId and no owner, so the only
// account that may read it is the one that owns the resume it was computed from.
public sealed class GetAnalysisByIdHandler(
    IAnalysisRepository analysisRepository,
    IResumeRepository resumeRepository)
    : IQueryHandler<GetAnalysisByIdQuery, Result<Analysis>>
{
    public async Task<Result<Analysis>> Handle(
        GetAnalysisByIdQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var analysis = await analysisRepository.GetByIdAsync(query.AnalysisId, cancellationToken);
            if (analysis is null)
                return Result<Analysis>.Failure("Analysis not found.");

            var resume = await resumeRepository.GetByIdAsync(analysis.ResumeId, cancellationToken);

            // "Analysis not found.", NOT "Resume not found.", and the choice does two jobs.
            //
            // It is the truthful answer to the route the caller used: it named an analysis, and an
            // analysis whose resume is gone is not readable — deleting a resume is a promise that
            // everything derived from it disappears with it, which ResumeRepository.DeleteAsync makes
            // by tombstoning the analyses in the same unit of work. Naming the resume instead would
            // report on a resource the caller did not ask about and does not own.
            //
            // It is also what keeps the two persistence providers observably identical. Under EF this
            // branch is unreachable: the cascade means an orphan never survives the query filter, so
            // the miss happens above. The in-memory store has no cascade to run, so the orphan does
            // survive and arrives here — and answers the same 404 with the same message. Api tests run
            // on that store; without this line they would certify a message SQL Server never sends.
            if (resume is null)
                return Result<Analysis>.Failure("Analysis not found.");

            // Owner only, matching ScoreResumeHandler rather than GetResumeHandler, which also admits
            // Role.Admin. The divergence is deliberate and is the narrower of the two: a score quotes
            // the candidate's resume and the posting back at them, so it is not the aggregate to widen
            // by reflex — and admitting admins would mean reaching for IAccountRepository from a
            // handler that otherwise needs nothing beyond the two rows it already reads.
            if (resume.OwnerId != query.RequesterId)
                return Result<Analysis>.Failure("Forbidden.");

            return Result<Analysis>.Success(analysis);
        }
        catch (DomainException ex)
        {
            return Result<Analysis>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<Analysis>.Failure(ex.Message);
        }
    }
}
