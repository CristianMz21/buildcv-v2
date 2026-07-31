namespace BuildCv.Application.Common.Repositories;

using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;

public interface IAnalysisRepository
{
    Task AddAsync(Analysis analysis, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Analysis>> GetByResumeIdAsync(ResumeId resumeId, CancellationToken cancellationToken = default);
}
