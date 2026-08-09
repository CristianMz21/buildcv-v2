namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddSkillCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string SkillName,
    SkillLevel? Level,
    int? YearsOfExperience,
    int? ReplacingItemId = null) : ICommand<Result<Resume>>;

public sealed class AddSkillHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddSkillCommand, Result<Resume>>
{
    public Task<Result<Resume>> Handle(AddSkillCommand command, CancellationToken cancellationToken = default) =>
        ResumeItemWrite.Execute(
            resumeRepository,
            command.RequesterId,
            command.ResumeId,
            ResumeSection.Skills,
            command.ReplacingItemId,
            () =>
            {
                var skill = Skill.Create(
                    Technology.Create(command.SkillName), command.Level, command.YearsOfExperience);
                return resume => resume.AddSkill(skill);
            },
            cancellationToken);
}
