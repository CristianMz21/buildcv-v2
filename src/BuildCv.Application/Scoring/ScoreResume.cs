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

            // Scoring is published-or-owned. Without this any authenticated caller could score against
            // any JobPostingId, including a stranger's unpublished draft -- and a score is a readable
            // summary of the posting: the requirements it names come back in the recommendations.
            //
            // Three consequences worth stating rather than discovering:
            //
            // 1. This is NARROWER than GetJobPostingQuery, which also admits Role.Admin and members of
            //    the owning organization. A recruiter's colleague can GET the draft but cannot score
            //    against it. The divergence is deliberate -- flagged rather than silently widened,
            //    because widening it would mean reaching for the account and organization repositories
            //    from a handler that needs neither.
            // 2. Closed and Archived are both != Published, so archiving a posting is a scoring kill
            //    switch for everyone except its owner. Probably right; it is still a choice.
            // 3. 404 and 403 stay distinguishable, matching GetJobPostingHandler: a caller who names a
            //    posting that does not exist is told so. Both handlers leak the same bit of existence
            //    information, and they leak it consistently.
            if (jobPosting.Status != JobPostingStatus.Published && jobPosting.OwnerId != command.RequesterId)
                return Result<Analysis>.Failure("Forbidden.");

            // ONE clock read for the whole run, and both the date scored against and the date stamped on
            // the row are derived from it.
            //
            // Two reads is what this was, and it is a bug rather than a style point: referenceDate feeds
            // ProfessionalDays, UnmarkedExperienceDays and ValidCertificateCount (see ScoringRules), so a
            // request that straddles midnight scored against yesterday and stamped ScoredAt with today.
            // The row then contradicts itself -- it says it was taken on a day it was not taken against --
            // and every predicate that reads the two together inherits the inconsistency.
            //
            // UtcDateTime, not DateTime: TimeProvider.GetUtcNow() is contractually UTC, so the two agree
            // for every provider that honours it, and UtcDateTime is the one that stays correct for a test
            // double that does not.
            var now = timeProvider.GetUtcNow();
            var referenceDate = DateOnly.FromDateTime(now.UtcDateTime);

            var score = scoringEngine.Score(resume, jobPosting, referenceDate);

            // The recommendations are persisted alongside the breakdown they were derived from. They
            // have to be: an Impact is only meaningful next to the score it was measured against, and a
            // history entry that stored the number without the advice could never answer "what was I
            // told to do about this, and did it work".
            var analysis = Analysis.Create(
                AnalysisId.New(),
                score.Breakdown,
                resume.Id,
                jobPosting.Id,
                now,
                score.Recommendations,
                // Read off the two aggregates this handler already loaded, so recording what was scored
                // costs no extra query.
                resume.UpdatedAt,
                jobPosting.UpdatedAt);
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
