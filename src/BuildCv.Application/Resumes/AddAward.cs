namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddAwardCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Title,
    string? Awarder,
    DateOnly? Date,
    string? Summary,
    int? ReplacingItemId = null) : ICommand<Result<Resume>>;

public sealed class AddAwardHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddAwardCommand, Result<Resume>>
{
    public Task<Result<Resume>> Handle(AddAwardCommand command, CancellationToken cancellationToken = default) =>
        ResumeItemWrite.Execute(
            resumeRepository,
            command.RequesterId,
            command.ResumeId,
            ResumeSection.Awards,
            command.ReplacingItemId,
            () =>
            {
                var award = new Award(
                    command.Title,
                    command.Awarder is null ? null : OrganizationName.Create(command.Awarder),
                    command.Date,
                    command.Summary);
                return resume => resume.AddAward(award);
            },
            cancellationToken);
}
