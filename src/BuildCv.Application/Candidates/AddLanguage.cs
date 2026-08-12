namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddLanguageCommand(
    AccountId RequesterId,
    string Name,
    string? Fluency,
    LanguageProficiency? Level,
    int? ReplacingItemId = null) : ICommand<Result<CandidateProfile>>;

public sealed class AddLanguageHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<AddLanguageCommand, Result<CandidateProfile>>
{
    public Task<Result<CandidateProfile>> Handle(
        AddLanguageCommand command, CancellationToken cancellationToken = default) =>
        CandidateProfileItemWrite.Execute(
            profileRepository,
            command.RequesterId,
            ResumeSection.Languages,
            command.ReplacingItemId,
            () =>
            {
                var language = Language.Create(command.Name, command.Fluency, command.Level);
                return profile => profile.AddLanguage(language);
            },
            cancellationToken);
}
