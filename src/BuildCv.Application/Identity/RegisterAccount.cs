namespace BuildCv.Application.Identity;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record RegisterAccountCommand(string Email, string Password, Role Role = Role.Candidate)
    : ICommand<Result<AccountDto>>;

public sealed class RegisterAccountHandler(
    IAccountRepository accountRepository,
    IPasswordHasher passwordHasher)
    : ICommandHandler<RegisterAccountCommand, Result<AccountDto>>
{
    // Registration is anonymous self-service, so the requested role is attacker-controlled.
    // Only these roles may be self-assigned; privileged roles are granted by an administrator
    // through ChangeRole, never by the registrant.
    private static bool IsSelfAssignable(Role role) => role is Role.Candidate or Role.Recruiter;

    public async Task<Result<AccountDto>> Handle(RegisterAccountCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsSelfAssignable(command.Role))
                return Result<AccountDto>.Failure("Role is not available for self-registration.");

            var email = Email.Create(command.Email);

            if (await accountRepository.ExistsByEmailAsync(email, cancellationToken))
                return Result<AccountDto>.Failure("Email is already registered.");

            var password = Password.Create(passwordHasher.Hash(command.Password));
            var account = Account.Create(email, password, command.Role);

            await accountRepository.AddAsync(account, cancellationToken);

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
