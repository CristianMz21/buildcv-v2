namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record GetAccountQuery(AccountId RequesterId, AccountId TargetId) : IQuery<Result<AccountDto>>;

public sealed class GetAccountHandler(IAccountRepository accountRepository)
    : IQueryHandler<GetAccountQuery, Result<AccountDto>>
{
    public async Task<Result<AccountDto>> Handle(GetAccountQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var requester = await accountRepository.GetByIdAsync(query.RequesterId, cancellationToken);
            if (requester is null)
                return Result<AccountDto>.Failure("Requester not found.");

            if (query.RequesterId != query.TargetId && requester.Role != Role.Admin)
                return Result<AccountDto>.Failure("Forbidden.");

            var target = await accountRepository.GetByIdAsync(query.TargetId, cancellationToken);
            if (target is null)
                return Result<AccountDto>.Failure("Account not found.");

            return Result<AccountDto>.Success(AccountDto.From(target));
        }
        catch (DomainException ex)
        {
            return Result<AccountDto>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<AccountDto>.Failure(ex.Message);
        }
    }
}
