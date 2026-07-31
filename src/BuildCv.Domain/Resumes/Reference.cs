using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Reference(
    string Name,
    string? Position,
    string? Company,
    Email? Email,
    PhoneNumber? PhoneNumber,
    string? ReferenceText);
