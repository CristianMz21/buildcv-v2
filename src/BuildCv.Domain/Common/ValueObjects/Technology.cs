using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

public sealed record Technology
{
    public string Name { get; }

    private Technology(string name) => Name = name;

    public static Technology Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Technology(name.Trim());
    }

    public static bool TryCreate(string name, out Technology? technology)
    {
        try
        {
            technology = Create(name);
            return true;
        }
        catch (Exception)
        {
            technology = null;
            return false;
        }
    }

    public override string ToString() => Name;

    public static implicit operator string(Technology technology) => technology.Name;
}
