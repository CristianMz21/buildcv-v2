namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record RenameResumeCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string? Name) : ICommand<Result<Resume>>;

/// <summary>
/// Names a CV, or clears the name when the request carries none.
/// </summary>
/// <remarks>
/// A ROUTE OF ITS OWN rather than a field on the contact update, because a name is not contact
/// information: it is what the candidate calls this CV among their others, and folding it into the
/// block that carries their email would make renaming require resending it.
/// </remarks>
public sealed class RenameResumeHandler(IResumeRepository resumeRepository)
    : ICommandHandler<RenameResumeCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(
        RenameResumeCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            // Blank clears rather than stores; the aggregate collapses the two states so no caller
            // has to.
            resume.Rename(command.Name);
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
