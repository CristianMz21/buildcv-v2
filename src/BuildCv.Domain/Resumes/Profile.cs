using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Domain.Resumes;

public sealed record Profile(
    string Network,
    string? Username,
    Url? Url);
