namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record GetCandidateProfileQuery(AccountId RequesterId)
    : IQuery<Result<CandidateProfileWithItemIds>>;

// THE READ A PROFILE EDITOR PERFORMS, AND THE ONLY READ THAT CARRIES ENTRY IDS — the profile twin of
// GetResume. Every other path to a profile (a generator selecting from it, an import merging into it)
// reads the aggregate without naming an entry, and none of them pays for the with-ids load. This one
// must answer "which bullet point is this one" so a later DELETE or PUT can address it: the collections
// are value objects and position is not identity across requests.
//
// THE OWNERSHIP CHECK IS PRESENT BUT STRUCTURALLY DEAD, and it is kept deliberately. The profile is
// looked up BY the requester's own owner id — there is no foreign id to aim, which is what makes the
// resume's check reachable there — so `OwnerId != RequesterId` can never be true from any repository
// this port allows. It is retained anyway because it is the same sentence GetResume states, expresses
// intent to a reader, and costs nothing; a future read that takes a profile id (an Admin view) is
// exactly the change that makes it load-bearing. There is no Admin escape here on purpose: even if the
// check ever fires, the act of reading someone else's personal profile requires more than a role.
public sealed class GetCandidateProfileHandler(ICandidateProfileRepository profileRepository)
    : IQueryHandler<GetCandidateProfileQuery, Result<CandidateProfileWithItemIds>>
{
    public async Task<Result<CandidateProfileWithItemIds>> Handle(
        GetCandidateProfileQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await profileRepository.GetByOwnerIdWithItemIdsAsync(query.RequesterId, cancellationToken);
            if (loaded is null)
                return Result<CandidateProfileWithItemIds>.Failure("Profile not found.");

            if (loaded.Profile.OwnerId != query.RequesterId)
                return Result<CandidateProfileWithItemIds>.Failure("Forbidden.");

            return Result<CandidateProfileWithItemIds>.Success(loaded);
        }
        catch (DomainException ex)
        {
            return Result<CandidateProfileWithItemIds>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<CandidateProfileWithItemIds>.Failure(ex.Message);
        }
    }
}
