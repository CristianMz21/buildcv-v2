namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddSkillCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string SkillName,
    SkillLevel? Level,
    int? YearsOfExperience) : ICommand<Result<Resume>>;

public sealed class AddSkillHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddSkillCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(AddSkillCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var skill = Skill.Create(
                Technology.Create(command.SkillName), command.Level, command.YearsOfExperience);
            resume.AddSkill(skill);
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
