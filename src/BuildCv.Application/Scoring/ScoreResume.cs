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

            // DE-DUPLICATION, and the Analyses table is the cache rather than something beside it.
            //
            // A candidate who reloads the page, or clicks Score twice, gets the score they already have
            // instead of a second identical row. That keeps the history a list of scoring EVENTS -- "what
            // changed when I edited my CV" -- rather than a list of button presses, which is the whole
            // read the product is for.
            //
            // It is deliberately NOT an IMemoryCache. This reads the same table, through the same
            // soft-delete filter, as every later read of the history, so it cannot disagree with what the
            // candidate is subsequently shown, and it is correct across instances by construction. An
            // in-process cache would also mean holding a decrypted resume aggregate past request scope.
            var existing = await analysisRepository.GetLatestByPairAsync(
                resume.Id, jobPosting.Id, cancellationToken);

            if (existing is not null && ScoredUnderTheSameConditions(existing, resume, jobPosting, referenceDate))
                return Result<Analysis>.Success(existing);

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

    // WHAT MAKES TWO SCORING RUNS THE SAME RUN. Four terms, and dropping any one of them silently returns
    // a stored number that the engine would not produce today.
    //
    // 1. THE RESUME VERSION -- the staleness predicate itself, CALLED rather than restated. The one
    //    outcome this must never produce is handing back a row the read path would immediately label
    //    stale, and sharing the single comparison is what makes that impossible rather than merely
    //    unlikely.
    // 2. THE POSTING VERSION. A recruiter adding a requirement changes the skills score of every
    //    candidate who has already been scored against it.
    // 3. THE SCORING MODEL. SchemaVersion covers weights AND formulas (see ScoringWeightsSnapshot), so a
    //    lexicon or a cap changing invalidates every stored score without a single input having moved.
    //    Compared against CurrentSchemaVersion, not against the loaded snapshot's neighbours: a
    //    renormalized v2 snapshot is no longer Default(), so the weights themselves cannot answer this.
    // 4. THE DATE. This is the one that looks droppable and is not: referenceDate feeds ProfessionalDays,
    //    UnmarkedExperienceDays and ValidCertificateCount, so the SAME resume against the SAME posting
    //    genuinely scores differently tomorrow -- experience accrues and certificates expire. A key
    //    without it would pin a candidate's score to the first day they ever ran it. The day is the
    //    granularity because the reference is a DateOnly; nothing finer can change the score.
    //
    // EQUALITY THROUGHOUT, never ">". UpdatedAt is written by whichever process handled the mutation, so
    // two writers with skewed clocks can leave a resume whose UpdatedAt is EARLIER than the one recorded
    // on the analysis, and ">" reads that as unchanged. Equality has no direction to get backwards.
    //
    // A null provenance timestamp equals nothing, so a row written before those columns existed is never
    // reused -- it is re-scored, which is the safe direction.
    private static bool ScoredUnderTheSameConditions(
        Analysis existing, Resume resume, JobPosting jobPosting, DateOnly referenceDate) =>
        !existing.IsStaleFor(resume.UpdatedAt)
        && existing.JobPostingUpdatedAt == jobPosting.UpdatedAt
        && existing.Breakdown.Weights.SchemaVersion == ScoringWeightsSnapshot.CurrentSchemaVersion
        && DateOnly.FromDateTime(existing.ScoredAt.UtcDateTime) == referenceDate;
}
