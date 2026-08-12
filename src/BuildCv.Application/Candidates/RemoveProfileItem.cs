namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record RemoveProfileItemCommand(
    AccountId RequesterId,
    ResumeSection Section,
    int ItemId) : ICommand<Result<CandidateProfile>>;

/// <summary>
/// Removes one entry from one of a profile's ten collections, named by the id
/// <c>GET /v1/profile</c> handed out.
/// </summary>
/// <remarks>
/// The mirror of <c>RemoveResumeItemHandler</c>, and one handler for ten routes for the same reason:
/// everything that could actually be got wrong — loading with ids, resolving an id that belongs to no
/// entry, refusing to touch the store when it does — is identical across sections. The one thing that
/// differs is <see cref="CandidateProfileItems.RemoveAt"/>, shared with the replace path so the two
/// cannot address an entry differently.
/// <para>
/// AN UNKNOWN ID IS "not found", NEVER "forbidden". Ids are unique only within one profile, so a
/// client holding a valid id of its own learns nothing by aiming it at somebody else's — and for this
/// aggregate there is nothing to aim: the profile is looked up BY the requester's owner id, so another
/// account's request simply finds no profile and answers "Profile not found." before any id is read.
/// </para>
/// </remarks>
public sealed class RemoveProfileItemHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<RemoveProfileItemCommand, Result<CandidateProfile>>
{
    public async Task<Result<CandidateProfile>> Handle(
        RemoveProfileItemCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await profileRepository.GetByOwnerIdWithItemIdsAsync(command.RequesterId, cancellationToken);
            if (loaded is null)
                return Result<CandidateProfile>.Failure("Profile not found.");

            var profile = loaded.Profile;
            if (profile.OwnerId != command.RequesterId)
                return Result<CandidateProfile>.Failure("Forbidden.");

            var position = loaded.ItemIds.PositionOf(command.Section, command.ItemId);
            if (position is null)
                return Result<CandidateProfile>.Failure($"{command.Section} entry not found.");

            CandidateProfileItems.RemoveAt(profile, command.Section, position.Value);

            await profileRepository.UpdateAsync(profile, cancellationToken);
            return Result<CandidateProfile>.Success(profile);
        }
        catch (DomainException ex)
        {
            return Result<CandidateProfile>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<CandidateProfile>.Failure(ex.Message);
        }
    }
}
