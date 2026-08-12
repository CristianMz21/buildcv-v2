namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddInterestCommand(
    AccountId RequesterId,
    string Name,
    string[] Keywords,
    int? ReplacingItemId = null) : ICommand<Result<CandidateProfile>>;

public sealed class AddInterestHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<AddInterestCommand, Result<CandidateProfile>>
{
    public Task<Result<CandidateProfile>> Handle(
        AddInterestCommand command, CancellationToken cancellationToken = default) =>
        CandidateProfileItemWrite.Execute(
            profileRepository,
            command.RequesterId,
            ResumeSection.Interests,
            command.ReplacingItemId,
            () =>
            {
                var interest = new Interest(command.Name) { Keywords = command.Keywords };
                return profile => profile.AddInterest(interest);
            },
            cancellationToken);
}
