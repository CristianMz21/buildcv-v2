namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddSkillCommand(
    AccountId RequesterId,
    string SkillName,
    SkillLevel? Level,
    int? YearsOfExperience,
    int? ReplacingItemId = null) : ICommand<Result<CandidateProfile>>;

public sealed class AddSkillHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<AddSkillCommand, Result<CandidateProfile>>
{
    public Task<Result<CandidateProfile>> Handle(
        AddSkillCommand command, CancellationToken cancellationToken = default) =>
        CandidateProfileItemWrite.Execute(
            profileRepository,
            command.RequesterId,
            ResumeSection.Skills,
            command.ReplacingItemId,
            () =>
            {
                var skill = Skill.Create(
                    Technology.Create(command.SkillName), command.Level, command.YearsOfExperience);
                return profile => profile.AddSkill(skill);
            },
            cancellationToken);
}
