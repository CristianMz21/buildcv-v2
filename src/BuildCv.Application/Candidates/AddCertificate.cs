namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddCertificateCommand(
    AccountId RequesterId,
    string Name,
    string Issuer,
    string? CredentialId,
    string? CredentialUrl,
    DateOnly? ValidityStart,
    DateOnly? ValidityEnd,
    int? ReplacingItemId = null) : ICommand<Result<CandidateProfile>>;

public sealed class AddCertificateHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<AddCertificateCommand, Result<CandidateProfile>>
{
    public Task<Result<CandidateProfile>> Handle(
        AddCertificateCommand command, CancellationToken cancellationToken = default) =>
        CandidateProfileItemWrite.Execute(
            profileRepository,
            command.RequesterId,
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
                return profile => profile.AddCertificate(certificate);
            },
            cancellationToken);
}
