namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record VerifyEmailCommand(AccountId RequesterId) : ICommand<Result<AccountDto>>;

public sealed class VerifyEmailHandler(IAccountRepository accountRepository)
    : ICommandHandler<VerifyEmailCommand, Result<AccountDto>>
{
    public async Task<Result<AccountDto>> Handle(VerifyEmailCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var account = await accountRepository.GetByIdAsync(command.RequesterId, cancellationToken);
            if (account is null)
                return Result<AccountDto>.Failure("Account not found.");

            account.VerifyEmail();
            await accountRepository.UpdateAsync(account, cancellationToken);

            return Result<AccountDto>.Success(AccountDto.From(account));
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
