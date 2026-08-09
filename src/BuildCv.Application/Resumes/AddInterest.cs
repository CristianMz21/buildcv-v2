namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddInterestCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Name,
    string[] Keywords,
    int? ReplacingItemId = null) : ICommand<Result<Resume>>;

public sealed class AddInterestHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddInterestCommand, Result<Resume>>
{
    public Task<Result<Resume>> Handle(AddInterestCommand command, CancellationToken cancellationToken = default) =>
        ResumeItemWrite.Execute(
            resumeRepository,
            command.RequesterId,
            command.ResumeId,
            ResumeSection.Interests,
            command.ReplacingItemId,
            () =>
            {
                var interest = new Interest(command.Name) { Keywords = command.Keywords };
                return resume => resume.AddInterest(interest);
            },
            cancellationToken);
}
