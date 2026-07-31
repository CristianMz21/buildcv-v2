using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Award(
    string Title,
    OrganizationName? Awarder,
    DateOnly? Date,
    string? Summary);
