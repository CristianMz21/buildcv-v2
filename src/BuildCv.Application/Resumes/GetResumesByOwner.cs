namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record GetResumesByOwnerQuery(AccountId RequesterId, AccountId OwnerId)
    : IQuery<Result<IReadOnlyList<Resume>>>;

public sealed class GetResumesByOwnerHandler(
    IResumeRepository resumeRepository,
    IAccountRepository accountRepository)
    : IQueryHandler<GetResumesByOwnerQuery, Result<IReadOnlyList<Resume>>>
{
    public async Task<Result<IReadOnlyList<Resume>>> Handle(GetResumesByOwnerQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            if (query.OwnerId != query.RequesterId)
            {
                var requester = await accountRepository.GetByIdAsync(query.RequesterId, cancellationToken);
                if (requester?.Role != Role.Admin)
                    return Result<IReadOnlyList<Resume>>.Failure("Forbidden.");
            }

            var resumes = await resumeRepository.GetByOwnerIdAsync(query.OwnerId, cancellationToken);
            return Result<IReadOnlyList<Resume>>.Success(resumes);
        }
        catch (DomainException ex)
        {
            return Result<IReadOnlyList<Resume>>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<IReadOnlyList<Resume>>.Failure(ex.Message);
        }
    }
}
