namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddLanguageCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Name,
    string? Fluency,
    LanguageProficiency? Level,
    int? ReplacingItemId = null) : ICommand<Result<Resume>>;

public sealed class AddLanguageHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddLanguageCommand, Result<Resume>>
{
    public Task<Result<Resume>> Handle(AddLanguageCommand command, CancellationToken cancellationToken = default) =>
        ResumeItemWrite.Execute(
            resumeRepository,
            command.RequesterId,
            command.ResumeId,
            ResumeSection.Languages,
            command.ReplacingItemId,
            () =>
            {
                var language = Language.Create(command.Name, command.Fluency, command.Level);
                return resume => resume.AddLanguage(language);
            },
            cancellationToken);
}
