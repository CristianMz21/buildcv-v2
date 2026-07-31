namespace BuildCv.Domain.Resumes;

public sealed record Basics(
    string FullName,
    string Email,
    string? PhoneNumber,
    string? Location,
    string? Website,
    string? Summary,
    PersonalInformation? PersonalInformation,
    IReadOnlyList<Profile> Profiles);
