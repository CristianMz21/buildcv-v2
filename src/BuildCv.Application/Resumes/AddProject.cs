namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
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
    string[] Highlights,
    int? ReplacingItemId = null) : ICommand<Result<Resume>>;

public sealed class AddProjectHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddProjectCommand, Result<Resume>>
{
    public Task<Result<Resume>> Handle(AddProjectCommand command, CancellationToken cancellationToken = default) =>
        ResumeItemWrite.Execute(
            resumeRepository,
            command.RequesterId,
            command.ResumeId,
            ResumeSection.Projects,
            command.ReplacingItemId,
            () =>
            {
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
                return resume => resume.AddProject(project);
            },
            cancellationToken);
}
