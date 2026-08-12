namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddProjectCommand(
    AccountId RequesterId,
    string Name,
    DateOnly Start,
    DateOnly? End,
    string? Description,
    string? RepositoryUrl,
    string? LiveDemoUrl,
    string[] Technologies,
    string[] Highlights,
    int? ReplacingItemId = null) : ICommand<Result<CandidateProfile>>;

public sealed class AddProjectHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<AddProjectCommand, Result<CandidateProfile>>
{
    public Task<Result<CandidateProfile>> Handle(
        AddProjectCommand command, CancellationToken cancellationToken = default) =>
        CandidateProfileItemWrite.Execute(
            profileRepository,
            command.RequesterId,
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
                return profile => profile.AddProject(project);
            },
            cancellationToken);
}
