namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddPublicationCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Title,
    string? Publisher,
    string? Url,
    DateOnly? ReleaseDate,
    string? Summary,
    int? ReplacingItemId = null) : ICommand<Result<Resume>>;

public sealed class AddPublicationHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddPublicationCommand, Result<Resume>>
{
    public Task<Result<Resume>> Handle(AddPublicationCommand command, CancellationToken cancellationToken = default) =>
        ResumeItemWrite.Execute(
            resumeRepository,
            command.RequesterId,
            command.ResumeId,
            ResumeSection.Publications,
            command.ReplacingItemId,
            () =>
            {
                var publication = new Publication(
                    command.Title,
                    command.Publisher is null ? null : OrganizationName.Create(command.Publisher),
                    command.Url is null ? null : Domain.Common.ValueObjects.Url.Create(command.Url),
                    command.ReleaseDate,
                    command.Summary);
                return resume => resume.AddPublication(publication);
            },
            cancellationToken);
}
