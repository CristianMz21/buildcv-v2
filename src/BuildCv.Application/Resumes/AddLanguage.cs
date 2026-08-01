namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddLanguageCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Name,
    string? Fluency,
    LanguageProficiency? Level) : ICommand<Result<Resume>>;

public sealed class AddLanguageHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddLanguageCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(AddLanguageCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var language = new Language(command.Name, command.Fluency, command.Level);
            resume.AddLanguage(language);
            await resumeRepository.UpdateAsync(resume, cancellationToken);

            return Result<Resume>.Success(resume);
        }
        catch (DomainException ex)
        {
            return Result<Resume>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<Resume>.Failure(ex.Message);
        }
    }
}
