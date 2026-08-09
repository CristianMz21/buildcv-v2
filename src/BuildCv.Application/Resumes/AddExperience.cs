namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddExperienceCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    ExperienceType Type,
    string Organization,
    string Position,
    DateOnly Start,
    DateOnly? End,
    string? Summary,
    int? ReplacingItemId = null) : ICommand<Result<Resume>>;

public sealed class AddExperienceHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddExperienceCommand, Result<Resume>>
{
    public Task<Result<Resume>> Handle(AddExperienceCommand command, CancellationToken cancellationToken = default) =>
        ResumeItemWrite.Execute(
            resumeRepository,
            command.RequesterId,
            command.ResumeId,
            ResumeSection.Experiences,
            command.ReplacingItemId,
            () =>
            {
                var experience = new Experience(
                    command.Type,
                    OrganizationName.Create(command.Organization),
                    command.Position,
                    DateRange.Create(command.Start, command.End),
                    command.Summary);
                return resume => resume.AddExperience(experience);
            },
            cancellationToken);
}
