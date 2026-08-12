namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;

/// <summary>
/// The write every "put an entry into one of a candidate profile's ten collections" use case performs,
/// whether it is appending a new entry or replacing an existing one.
/// </summary>
/// <remarks>
/// THE PROFILE HALF OF <see cref="ItemWrite"/>, the mirror of <see cref="Resumes.ResumeItemWrite"/>.
/// The plumbing — load/check/remove/save, the order of remove-before-add, "not found" never
/// "forbidden" — is the generic core and is not restated here. This type supplies the profile part:
/// the two-shape load (<see cref="ICandidateProfileRepository.GetByOwnerIdAsync"/> for an append,
/// <see cref="ICandidateProfileRepository.GetByOwnerIdWithItemIdsAsync"/> for a replace) and
/// <see cref="CandidateProfileItems.RemoveAt"/>, so the two aggregates cannot address an entry
/// differently.
/// <para>
/// ONE DIFFERENCE FROM THE RESUME TWIN IS LOAD-BEARING: the profile is addressed BY ITS OWNER, not by a
/// route id. The load is keyed with <paramref name="requesterId"/>, so the ownership check the core
/// runs is structurally dead here — a profile can only ever be found under the account that asked — and
/// an account that has never created one answers "Profile not found." rather than anything that could
/// be called forbidden. That is the security property of the whole surface, and it is stronger than the
/// resume's: there is no id to aim at somebody else.
/// </para>
/// </remarks>
internal static class CandidateProfileItemWrite
{
    public static Task<Result<CandidateProfile>> Execute(
        ICandidateProfileRepository profileRepository,
        AccountId requesterId,
        ResumeSection section,
        int? replacingItemId,
        Func<Action<CandidateProfile>> build,
        CancellationToken cancellationToken)
        => ItemWrite.Execute(
            load: async token =>
            {
                // An append needs no ids, and GetByOwnerIdWithItemIdsAsync exists precisely so that the
                // paths which do not address an entry are spared the per-entry id walk. See its remarks.
                if (replacingItemId is null)
                {
                    var profile = await profileRepository.GetByOwnerIdAsync(requesterId, token);
                    return (profile, (ResumeItemIds?)null);
                }

                var loaded = await profileRepository.GetByOwnerIdWithItemIdsAsync(requesterId, token);
                return loaded is null ? (null, null) : (loaded.Profile, loaded.ItemIds);
            },
            ownerIdOf: profile => profile.OwnerId,
            removeAt: CandidateProfileItems.RemoveAt,
            save: profileRepository.UpdateAsync,
            requesterId: requesterId,
            section: section,
            replacingItemId: replacingItemId,
            notFoundMessage: "Profile not found.",
            build: build,
            cancellationToken: cancellationToken);
}

/// <summary>
/// Addresses one entry of one of a profile's ten collections by its position.
/// </summary>
/// <remarks>
/// BY POSITION, not by value, and for the same reasons as <c>ResumeItems.RemoveAt</c>: these are
/// duplicates-allowed value objects, so removing by value would take the first match, which is an entry
/// the caller never named. The switch is exhaustive over a closed enum, so a collection added to the
/// aggregate without a case here does not compile.
/// </remarks>
internal static class CandidateProfileItems
{
    public static void RemoveAt(CandidateProfile profile, ResumeSection section, int position)
    {
        switch (section)
        {
            case ResumeSection.Experiences: profile.RemoveExperienceAt(position); break;
            case ResumeSection.Educations: profile.RemoveEducationAt(position); break;
            case ResumeSection.Skills: profile.RemoveSkillAt(position); break;
            case ResumeSection.Projects: profile.RemoveProjectAt(position); break;
            case ResumeSection.Certificates: profile.RemoveCertificateAt(position); break;
            case ResumeSection.Languages: profile.RemoveLanguageAt(position); break;
            case ResumeSection.Awards: profile.RemoveAwardAt(position); break;
            case ResumeSection.Publications: profile.RemovePublicationAt(position); break;
            case ResumeSection.Interests: profile.RemoveInterestAt(position); break;
            case ResumeSection.References: profile.RemoveReferenceAt(position); break;
            default: throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown section.");
        }
    }
}
