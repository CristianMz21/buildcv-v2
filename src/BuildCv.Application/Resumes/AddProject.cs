namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddProjectCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Name,
    DateOnly Start,
    DateOnly? End,
    string? Description,
    string? RepositoryUrl,
    string? LiveDemoUrl,
    string[] Technologies,
    string[] Highlights) : ICommand<Result<Resume>>;

public sealed class AddProjectHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddProjectCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(AddProjectCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var project = new Project(
                command.Name,
                DateRange.Create(command.Start, command.End),
                command.Description,
                command.RepositoryUrl is null ? null : Url.Create(command.RepositoryUrl),
                command.LiveDemoUrl is null ? null : Url.Create(command.LiveDemoUrl))
            {
                Technologies = command.Technologies.Select(Technology.Create).ToList(),
                Highlights = command.Highlights,
            };
            resume.AddProject(project);
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
