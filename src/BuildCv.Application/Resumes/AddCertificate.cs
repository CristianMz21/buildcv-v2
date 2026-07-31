namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddCertificateCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Name,
    string Issuer,
    string? CredentialId,
    string? CredentialUrl,
    DateOnly? ValidityStart,
    DateOnly? ValidityEnd) : ICommand<Result<Resume>>;

public sealed class AddCertificateHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddCertificateCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(AddCertificateCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var certificate = new Certificate(
                command.Name,
                OrganizationName.Create(command.Issuer),
                command.CredentialId,
                command.CredentialUrl is null ? null : Url.Create(command.CredentialUrl),
                command.ValidityStart is null ? null : DateRange.Create(command.ValidityStart.Value, command.ValidityEnd));
            resume.AddCertificate(certificate);
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
