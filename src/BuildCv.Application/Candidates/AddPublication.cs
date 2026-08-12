namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddPublicationCommand(
    AccountId RequesterId,
    string Title,
    string? Publisher,
    string? Url,
    DateOnly? ReleaseDate,
    string? Summary,
    int? ReplacingItemId = null) : ICommand<Result<CandidateProfile>>;

public sealed class AddPublicationHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<AddPublicationCommand, Result<CandidateProfile>>
{
    public Task<Result<CandidateProfile>> Handle(
        AddPublicationCommand command, CancellationToken cancellationToken = default) =>
        CandidateProfileItemWrite.Execute(
            profileRepository,
            command.RequesterId,
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
                return profile => profile.AddPublication(publication);
            },
            cancellationToken);
}
