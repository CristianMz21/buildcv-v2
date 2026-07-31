using BuildCv.Domain.Exceptions;

namespace BuildCv.Domain.Common.ValueObjects;

public sealed record Url
{
    public string Value { get; }
    public Uri Uri { get; }

    private Url(string value, Uri uri)
    {
        Value = value;
        Uri = uri;
    }

    public static Url Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidUrlException($"Invalid URL format: {value}");

        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidUrlException($"URL must use http or https scheme: {value}");

        return new Url(value, uri);
    }

    public static bool TryCreate(string value, out Url? url)
    {
        try
        {
            url = Create(value);
            return true;
        }
        catch (DomainException)
        {
            url = null;
            return false;
        }
    }

    public override string ToString() => Value;

    public static implicit operator string(Url url) => url.Value;
}
