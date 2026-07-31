namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record UpdateContactInformationCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string FullName,
    string Email,
    string? PhoneNumber,
    string? Location,
    string? Summary) : ICommand<Result<Resume>>;

public sealed class UpdateContactInformationHandler(IResumeRepository resumeRepository)
    : ICommandHandler<UpdateContactInformationCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(UpdateContactInformationCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var contact = ContactInformationFactory.Create(
                command.FullName, command.Email, command.PhoneNumber, command.Location, command.Summary);

            resume.UpdateContactInformation(contact);
            await resumeRepository.UpdateAsync(resume, cancellationToken);

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
