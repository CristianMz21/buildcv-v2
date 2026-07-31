namespace BuildCv.Domain.Resumes;

public sealed record Reference(
    string Name,
    string? Position,
    string? Company,
    string? Email,
    string? PhoneNumber,
    string? ReferenceText);
