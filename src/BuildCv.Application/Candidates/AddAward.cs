namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddAwardCommand(
    AccountId RequesterId,
    string Title,
    string? Awarder,
    DateOnly? Date,
    string? Summary,
    int? ReplacingItemId = null) : ICommand<Result<CandidateProfile>>;

public sealed class AddAwardHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<AddAwardCommand, Result<CandidateProfile>>
{
    public Task<Result<CandidateProfile>> Handle(
        AddAwardCommand command, CancellationToken cancellationToken = default) =>
        CandidateProfileItemWrite.Execute(
            profileRepository,
            command.RequesterId,
            ResumeSection.Awards,
            command.ReplacingItemId,
            () =>
            {
                var award = new Award(
                    command.Title,
                    command.Awarder is null ? null : OrganizationName.Create(command.Awarder),
                    command.Date,
                    command.Summary);
                return profile => profile.AddAward(award);
            },
            cancellationToken);
}
