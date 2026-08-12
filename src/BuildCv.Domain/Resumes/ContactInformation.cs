using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record ContactInformation(
    PersonName FullName,
    Email Email,
    PhoneNumber? PhoneNumber = null,
    string? Location = null,
    Url? Website = null,
    string? Summary = null)
{
    public IReadOnlyList<Profile> Profiles { get; init; } = [];

    /// <summary>
    /// Merges the incoming contact into the existing one, in the direction that never destroys data:
    /// the EXISTING value wins field by field, and the incoming value fills only the fields the
    /// existing one does not have.
    /// </summary>
    /// <remarks>
    /// The profile is master data a candidate typed or corrected by hand; an import is a convenience
    /// source that must never take a field back. Re-importing the same document is therefore a no-op
    /// for contact, and a first import populates everything because the profile was empty.
    /// <see cref="Profiles"/> is kept when it is non-empty for the same reason.
    /// </remarks>
    public static ContactInformation GapFill(ContactInformation existing, ContactInformation incoming) =>
        new(
            existing.FullName,
            existing.Email,
            existing.PhoneNumber ?? incoming.PhoneNumber,
            existing.Location ?? incoming.Location,
            existing.Website ?? incoming.Website,
            existing.Summary ?? incoming.Summary)
        {
            Profiles = existing.Profiles.Count > 0 ? existing.Profiles : incoming.Profiles,
        };
}
