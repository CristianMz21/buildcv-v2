namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddEducationCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Institution,
    string? Degree,
    string? FieldOfStudy,
    DateOnly Start,
    DateOnly? End,
    string? Grade,
    EducationLevel? Level) : ICommand<Result<Resume>>;

public sealed class AddEducationHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddEducationCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(AddEducationCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var education = new Education(
                OrganizationName.Create(command.Institution),
                command.Degree,
                command.FieldOfStudy,
                DateRange.Create(command.Start, command.End),
                command.Grade,
                command.Level);
            resume.AddEducation(education);
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
