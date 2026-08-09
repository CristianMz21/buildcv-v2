namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
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
    string? ReferenceText,
    int? ReplacingItemId = null) : ICommand<Result<Resume>>;

public sealed class AddReferenceHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddReferenceCommand, Result<Resume>>
{
    public Task<Result<Resume>> Handle(AddReferenceCommand command, CancellationToken cancellationToken = default) =>
        ResumeItemWrite.Execute(
            resumeRepository,
            command.RequesterId,
            command.ResumeId,
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
                return resume => resume.AddReference(reference);
            },
            cancellationToken);
}
