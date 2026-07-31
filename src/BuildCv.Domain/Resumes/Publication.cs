using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Publication(
    string Title,
    OrganizationName? Publisher,
    Url? Url,
    DateOnly? ReleaseDate,
    string? Summary);
