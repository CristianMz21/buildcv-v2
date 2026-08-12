namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddReferenceCommand(
    AccountId RequesterId,
    string Name,
    string? Position,
    string? Company,
    string? Email,
    string? PhoneNumber,
    string? ReferenceText,
    int? ReplacingItemId = null) : ICommand<Result<CandidateProfile>>;

public sealed class AddReferenceHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<AddReferenceCommand, Result<CandidateProfile>>
{
    public Task<Result<CandidateProfile>> Handle(
        AddReferenceCommand command, CancellationToken cancellationToken = default) =>
        CandidateProfileItemWrite.Execute(
            profileRepository,
            command.RequesterId,
            ResumeSection.References,
            command.ReplacingItemId,
            () =>
            {
                var reference = new Reference(
                    command.Name,
                    command.Position,
                    command.Company is null ? null : OrganizationName.Create(command.Company),
                    command.Email is null ? null : Domain.Common.ValueObjects.Email.Create(command.Email),
                    command.PhoneNumber is null ? null : Domain.Common.ValueObjects.PhoneNumber.Create(command.PhoneNumber),
                    command.ReferenceText);
                return profile => profile.AddReference(reference);
            },
            cancellationToken);
}
