namespace BuildCv.Application.Common.Services;

using BuildCv.Domain.Jobs;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

public interface IScoringEngine
{
    ScoreBreakdown Score(Resume resume, JobPosting jobPosting, DateOnly referenceDate);
}
