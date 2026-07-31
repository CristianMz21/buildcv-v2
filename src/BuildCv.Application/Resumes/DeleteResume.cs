namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record DeleteResumeCommand(AccountId RequesterId, ResumeId ResumeId) : ICommand<Result<ResumeId>>;

public sealed class DeleteResumeHandler(IResumeRepository resumeRepository)
    : ICommandHandler<DeleteResumeCommand, Result<ResumeId>>
{
    public async Task<Result<ResumeId>> Handle(DeleteResumeCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await resumeRepository.GetByIdAsync(command.ResumeId, cancellationToken);
            if (resume is null)
                return Result<ResumeId>.Failure("Resume not found.");

            if (resume.OwnerId != command.RequesterId)
                return Result<ResumeId>.Failure("Forbidden.");

            await resumeRepository.DeleteAsync(command.ResumeId, cancellationToken);

            return Result<ResumeId>.Success(command.ResumeId);
        }
        catch (DomainException ex)
        {
            return Result<ResumeId>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<ResumeId>.Failure(ex.Message);
        }
    }
}
