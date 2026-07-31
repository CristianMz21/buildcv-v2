namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddPublicationCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    string Title,
    string? Publisher,
    string? Url,
    DateOnly? ReleaseDate,
    string? Summary) : ICommand<Result<Resume>>;

public sealed class AddPublicationHandler(IResumeRepository resumeRepository)
    : ICommandHandler<AddPublicationCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(AddPublicationCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<Resume>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var publication = new Publication(
                command.Title,
                command.Publisher is null ? null : OrganizationName.Create(command.Publisher),
                command.Url is null ? null : Domain.Common.ValueObjects.Url.Create(command.Url),
                command.ReleaseDate,
                command.Summary);
            resume.AddPublication(publication);
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
