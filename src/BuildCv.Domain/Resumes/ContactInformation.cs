using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record ContactInformation(
    PersonName FullName,
    Email Email,
    PhoneNumber? PhoneNumber = null,
    string? Location = null,
    Url? Website = null,
    string? Summary = null,
    Demographics? Demographics = null)
{
    public IReadOnlyList<Profile> Profiles { get; init; } = [];
}
