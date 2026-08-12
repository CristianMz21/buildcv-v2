namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddExperienceCommand(
    AccountId RequesterId,
    ExperienceType Type,
    string Organization,
    string Position,
    DateOnly Start,
    DateOnly? End,
    string? Summary,
    int? ReplacingItemId = null) : ICommand<Result<CandidateProfile>>;

public sealed class AddExperienceHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<AddExperienceCommand, Result<CandidateProfile>>
{
    public Task<Result<CandidateProfile>> Handle(
        AddExperienceCommand command, CancellationToken cancellationToken = default) =>
        CandidateProfileItemWrite.Execute(
            profileRepository,
            command.RequesterId,
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
                return profile => profile.AddExperience(experience);
            },
            cancellationToken);
}
