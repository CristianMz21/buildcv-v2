namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record RemoveResumeItemCommand(
    AccountId RequesterId,
    ResumeId ResumeId,
    ResumeSection Section,
    int ItemId) : ICommand<Result<Resume>>;

/// <summary>
/// Removes one entry from one of a resume's ten collections, named by the id
/// <c>GET /v1/resumes/{id}</c> handed out.
/// </summary>
/// <remarks>
/// <para>
/// ONE HANDLER FOR TEN ROUTES, against this repository's usual one-file-per-use-case grain, and the
/// reason is that the ten differ in exactly one expression. Everything that could actually be got
/// wrong — loading with ids, the ownership check, resolving an id that belongs to no entry, refusing
/// to touch the store when it does not — is identical, and ten copies of it is how one of them
/// silently loses the ownership check. The switch is exhaustive over a closed enum, so a collection
/// added to the aggregate without a case here does not compile.
/// </para>
/// <para>
/// AN UNKNOWN ID IS "not found", NEVER "forbidden", and it is resolved against the aggregate the
/// requester was already allowed to load. Ids are unique only within one resume, so a client holding a
/// valid id for its OWN resume learns nothing by aiming it at somebody else's: the ownership check
/// runs first and answers before any id is looked at.
/// </para>
/// </remarks>
public sealed class RemoveResumeItemHandler(IResumeRepository resumeRepository)
    : ICommandHandler<RemoveResumeItemCommand, Result<Resume>>
{
    public async Task<Result<Resume>> Handle(
        RemoveResumeItemCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await resumeRepository.GetByIdWithItemIdsAsync(command.ResumeId, cancellationToken);
            if (loaded is null)
                return Result<Resume>.Failure("Resume not found.");

            var resume = loaded.Resume;
            if (resume.OwnerId != command.RequesterId)
                return Result<Resume>.Failure("Forbidden.");

            var position = loaded.ItemIds.PositionOf(command.Section, command.ItemId);
            if (position is null)
                return Result<Resume>.Failure($"{command.Section} entry not found.");

            // BY POSITION, not by value. Six of these collections accept duplicates, and removing by
            // value takes the first match — which would delete an entry the caller never named while
            // leaving the one it did. See the remarks on Resume.RemoveAt.
            Remove(resume, command.Section, position.Value);

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

    private static void Remove(Resume resume, ResumeSection section, int position)
    {
        switch (section)
        {
            case ResumeSection.Experiences: resume.RemoveExperienceAt(position); break;
            case ResumeSection.Educations: resume.RemoveEducationAt(position); break;
            case ResumeSection.Skills: resume.RemoveSkillAt(position); break;
            case ResumeSection.Projects: resume.RemoveProjectAt(position); break;
            case ResumeSection.Certificates: resume.RemoveCertificateAt(position); break;
            case ResumeSection.Languages: resume.RemoveLanguageAt(position); break;
            case ResumeSection.Awards: resume.RemoveAwardAt(position); break;
            case ResumeSection.Publications: resume.RemovePublicationAt(position); break;
            case ResumeSection.Interests: resume.RemoveInterestAt(position); break;
            case ResumeSection.References: resume.RemoveReferenceAt(position); break;
            default: throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown section.");
        }
    }
}
