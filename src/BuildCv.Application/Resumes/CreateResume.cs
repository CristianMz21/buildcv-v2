namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record CreateResumeCommand(
    AccountId RequesterId,
    string FullName,
    string Email,
    string? PhoneNumber,
    string? Location,
    string? Summary) : ICommand<Result<Resume>>;

public sealed class CreateResumeHandler(IResumeRepository resumeRepository)
    : ICommandHandler<CreateResumeCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(CreateResumeCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var contact = ContactInformationFactory.Create(
                command.FullName, command.Email, command.PhoneNumber, command.Location, command.Summary);

            var resume = Resume.Create(command.RequesterId, contact);
            await resumeRepository.AddAsync(resume, cancellationToken);

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

internal static class ContactInformationFactory
{
    internal static ContactInformation Create(
        string fullName, string email, string? phoneNumber, string? location, string? summary) =>
        new(
            PersonName.Create(fullName),
            Email.Create(email),
            phoneNumber is null ? null : PhoneNumber.Create(phoneNumber),
            location,
            null,
            summary);
}
