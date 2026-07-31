namespace BuildCv.Domain.Resumes;

public sealed record Interest(
    string Name)
{
    public IReadOnlyList<string> Keywords { get; init; } = [];
}
