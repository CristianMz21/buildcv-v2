using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Publication(
    string Title,
    string? Publisher,
    Url? Url,
    DateOnly? ReleaseDate,
    string? Summary);
