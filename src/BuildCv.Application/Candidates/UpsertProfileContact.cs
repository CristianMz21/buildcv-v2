namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

public sealed record UpsertProfileContactCommand(
    AccountId RequesterId,
    string FullName,
    string Email,
    string? PhoneNumber,
    string? Location,
    string? Summary) : ICommand<Result<CandidateProfile>>;

// The only writer of a profile's contact that CREATES the profile. Three facts shape it:
//
// - The profile is keyed by owner, not by a route id, so "the account has none" is a normal first visit
//   rather than a 404: the contact update is the natural place a candidate first types who they are, and
//   creating here is what lets every later profile write assume the aggregate exists.
// - WEBSITE AND PROFILES ARE CARRIED OVER, exactly as UpdateContactInformationHandler does for a resume.
//   ContactInformationFactory hardcodes a null Website and empty Profiles and never widens its five
//   fields, so rebuilding the contact from this command's shape alone would silently erase the site and
//   every social handle the import path wrote. This command deliberately does not ACCEPT them either; a
//   "not sent" field must mean "unchanged" rather than "deleted".
// - A no-op edit must not bump the profile: UpdateContactInformation already leaves equal contact alone,
//   so the save below is only a write when something actually changed.
// - The create is keyed by owner, not by id, so two CONCURRENT first-time calls both read null and both
//   AddAsync — the second bumps into the unique owner index and throws, a plain Exception this handler
//   does not catch (the resume twin is immune because every create gets a fresh ResumeId). Window is
//   one account's own first visit; re-reading and succeeding on the conflict is the fix if it ever bites.
public sealed class UpsertProfileContactHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<UpsertProfileContactCommand, Result<CandidateProfile>>
{
    public async Task<Result<CandidateProfile>> Handle(
        UpsertProfileContactCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var contact = ContactInformationFactory.Create(
                command.FullName, command.Email, command.PhoneNumber, command.Location, command.Summary);

            var profile = await profileRepository.GetByOwnerIdAsync(command.RequesterId, cancellationToken);
            if (profile is null)
            {
                var created = CandidateProfile.Create(command.RequesterId, contact);
                await profileRepository.AddAsync(created, cancellationToken);
                return Result<CandidateProfile>.Success(created);
            }

            profile.UpdateContactInformation(contact with
            {
                Website = profile.ContactInformation.Website,
                Profiles = profile.ContactInformation.Profiles,
            });
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
