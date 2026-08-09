namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
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
    DateOnly? ValidityEnd,
    int? ReplacingItemId = null) : ICommand<Result<Resume>>;

public sealed class AddCertificateHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddCertificateCommand, Result<Resume>>
{
    public Task<Result<Resume>> Handle(AddCertificateCommand command, CancellationToken cancellationToken = default) =>
        ResumeItemWrite.Execute(
            resumeRepository,
            command.RequesterId,
            command.ResumeId,
            ResumeSection.Certificates,
            command.ReplacingItemId,
            () =>
            {
                var certificate = new Certificate(
                    command.Name,
                    OrganizationName.Create(command.Issuer),
                    command.CredentialId,
                    command.CredentialUrl is null ? null : Url.Create(command.CredentialUrl),
                    command.ValidityStart is null ? null : DateRange.Create(command.ValidityStart.Value, command.ValidityEnd));
                return resume => resume.AddCertificate(certificate);
            },
            cancellationToken);
}
