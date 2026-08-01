namespace BuildCv.Application.Scoring;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

public sealed record ScoreResumeCommand(AccountId RequesterId, ResumeId ResumeId, JobPostingId JobPostingId)
    : ICommand<Result<Analysis>>;

public sealed class ScoreResumeHandler(
    IResumeRepository resumeRepository,
    IJobPostingRepository jobPostingRepository,
    IAnalysisRepository analysisRepository,
    IScoringEngine scoringEngine,
    TimeProvider timeProvider)
    : ICommandHandler<ScoreResumeCommand, Result<Analysis>>
{
    public async Task<Result<Analysis>> Handle(ScoreResumeCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Analysis>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Analysis>.Failure("Forbidden.");

            var jobPosting = await jobPostingRepository.GetByIdAsync(command.JobPostingId, cancellationToken);
            if (jobPosting is null)
                return Result<Analysis>.Failure("Job posting not found.");

            var referenceDate = DateOnly.FromDateTime(timeProvider.GetUtcNow().DateTime);
            var breakdown = scoringEngine.Score(resume, jobPosting, referenceDate);

            var analysis = Analysis.Create(
                AnalysisId.New(), breakdown, resume.Id, jobPosting.Id, timeProvider.GetUtcNow());
            await analysisRepository.AddAsync(analysis, cancellationToken);

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
