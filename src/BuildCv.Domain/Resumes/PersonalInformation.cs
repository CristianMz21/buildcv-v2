namespace BuildCv.Domain.Resumes;

public sealed record PersonalInformation(
    string? NationalId,
    string? Nationality,
    string? MaritalStatus,
    string? MilitaryServiceNumber,
    string? BloodType);
