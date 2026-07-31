using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Education(
    string Institution,
    string? Degree,
    string? FieldOfStudy,
    DateRange Period,
    string? Grade);
