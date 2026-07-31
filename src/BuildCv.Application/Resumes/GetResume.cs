namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record GetResumeQuery(AccountId RequesterId, ResumeId ResumeId) : IQuery<Result<Resume>>;

public sealed class GetResumeHandler(
    IResumeRepository resumeRepository,
    IAccountRepository accountRepository)
    : IQueryHandler<GetResumeQuery, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(GetResumeQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(query.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != query.RequesterId)
            {
                var requester = await accountRepository.GetByIdAsync(query.RequesterId, cancellationToken);
                if (requester?.Role != Role.Admin)
                    return Result<Resume>.Failure("Forbidden.");
            }

            return Result<Resume>.Success(resume);
        }
        catch (DomainException ex)
        {
            return Result<Resume>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<Resume>.Failure(ex.Message);
        }
    }
}
