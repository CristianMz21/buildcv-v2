namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddReferenceCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Name,
    string? Position,
    string? Company,
    string? Email,
    string? PhoneNumber,
    string? ReferenceText) : ICommand<Result<Resume>>;

public sealed class AddReferenceHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddReferenceCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(AddReferenceCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var reference = new Reference(
                command.Name,
                command.Position,
                command.Company is null ? null : OrganizationName.Create(command.Company),
                command.Email is null ? null : Domain.Common.ValueObjects.Email.Create(command.Email),
                command.PhoneNumber is null ? null : Domain.Common.ValueObjects.PhoneNumber.Create(command.PhoneNumber),
                command.ReferenceText);
            resume.AddReference(reference);
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
