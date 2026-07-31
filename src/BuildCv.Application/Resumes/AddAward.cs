namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddAwardCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Title,
    string? Awarder,
    DateOnly? Date,
    string? Summary) : ICommand<Result<Resume>>;

public sealed class AddAwardHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddAwardCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(AddAwardCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var award = new Award(
                command.Title,
                command.Awarder is null ? null : OrganizationName.Create(command.Awarder),
                command.Date,
                command.Summary);
            resume.AddAward(award);
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
