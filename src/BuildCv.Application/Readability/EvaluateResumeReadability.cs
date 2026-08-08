namespace BuildCv.Application.Readability;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;

public sealed record EvaluateResumeReadabilityCommand(AccountId RequesterId, ResumeId ResumeId)
    : ICommand<Result<ReadabilityReport>>;

// ONE READ, and it is the authorization read. This is the whole shape of the handler that makes
// readability the half of the product a candidate can use immediately: no posting is loaded, none is
// named in the command, and the request succeeds with zero job offers in the entire system.
//
// Owner only, matching ScoreResumeHandler rather than GetResumeHandler, which also admits Role.Admin.
// The divergence is deliberate and is the narrower of the two: the advice quotes the candidate's resume
// back at them, so it is not the aggregate to widen by reflex.
public sealed class EvaluateResumeReadabilityHandler(
    IResumeRepository resumeRepository,
    IReadabilityReportRepository readabilityReportRepository,
    IReadabilityEngine readabilityEngine,
    TimeProvider timeProvider)
    : ICommandHandler<EvaluateResumeReadabilityCommand, Result<ReadabilityReport>>
{
    public async Task<Result<ReadabilityReport>> Handle(
        EvaluateResumeReadabilityCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<ReadabilityReport>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<ReadabilityReport>.Failure("Forbidden.");

            // ONE clock read for the whole run, and both the date evaluated against and the date stamped
            // on the row are derived from it -- the bug ScoreResumeHandler already fixed on its side.
            // referenceDate feeds the employment-gap walk, so a request that straddled midnight would
            // measure the timeline against yesterday and stamp EvaluatedAt with today, leaving a row that
            // contradicts itself.
            //
            // UtcDateTime, not DateTime: TimeProvider.GetUtcNow() is contractually UTC, so the two agree
            // for every provider that honours it, and UtcDateTime is the one that stays correct for a
            // test double that does not.
            var now = timeProvider.GetUtcNow();
            var referenceDate = DateOnly.FromDateTime(now.UtcDateTime);

            var result = readabilityEngine.Evaluate(resume, referenceDate);

            // The recommendations are persisted alongside the breakdown they were derived from. They
            // have to be: an Impact is only meaningful next to the score it was measured against, and a
            // history entry that stored the number without the advice could never answer "what was I
            // told to do about this, and did it work".
            var report = ReadabilityReport.Create(
                ReadabilityReportId.New(),
                result.Breakdown,
                resume.Id,
                now,
                result.Recommendations);

            // NO DE-DUPLICATION, unlike ScoreResumeHandler, and the omission is scoped rather than
            // forgotten: the key that would decide it needs a read of the latest report for this resume,
            // which is a port method nothing else calls yet. Every request therefore writes a row. That
            // is the same behaviour the scoring endpoint had before its de-duplication landed, and it is
            // the safe direction -- a duplicate row is a wasted insert, while a reused row that should
            // not have been is a candidate shown a number about a CV they no longer have.
            await readabilityReportRepository.AddAsync(report, cancellationToken);

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
