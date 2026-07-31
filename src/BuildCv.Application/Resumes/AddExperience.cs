namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
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
    string? Summary) : ICommand<Result<Resume>>;

public sealed class AddExperienceHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddExperienceCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(AddExperienceCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var experience = new Experience(
                command.Type,
                OrganizationName.Create(command.Organization),
                command.Position,
                DateRange.Create(command.Start, command.End),
                command.Summary);
            resume.AddExperience(experience);
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
