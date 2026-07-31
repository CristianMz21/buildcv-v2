namespace BuildCv.Domain.Resumes;

public sealed record Demographics(
    string? NationalId,
    string? Nationality,
    string? MaritalStatus,
    string? MilitaryServiceNumber,
    string? BloodType);
