namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record ChangePasswordCommand(AccountId RequesterId, string CurrentPassword, string NewPassword)
    : ICommand<Result<AccountDto>>;

public sealed class ChangePasswordHandler(
    IAccountRepository accountRepository,
    IPasswordHasher passwordHasher)
    : ICommandHandler<ChangePasswordCommand, Result<AccountDto>>
{
    public async Task<Result<AccountDto>> Handle(ChangePasswordCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var account = await accountRepository.GetByIdAsync(command.RequesterId, cancellationToken);
            if (account is null)
                return Result<AccountDto>.Failure("Account not found.");

            if (!passwordHasher.Verify(command.CurrentPassword, account.Password.Hash))
                return Result<AccountDto>.Failure("Current password is incorrect.");

            account.ChangePassword(Password.Create(passwordHasher.Hash(command.NewPassword)));
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
