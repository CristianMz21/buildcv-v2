using System.Text.Json;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// The small, always-loaded lists that hang off resume entries are stored as one JSON column rather
// than child tables: nothing queries them individually and they are replaced wholesale.
//
// Domain value objects are written as their primitive projection and rebuilt through their factories
// on the way back, so data that no longer satisfies a Domain invariant fails loudly at load instead
// of materializing an invalid aggregate.
//
// The codec is exposed separately from the converters so encrypted list columns can compose it with
// EncryptedConverter<T> — JSON first, then the envelope.
internal static class JsonListCodec
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string ToJson(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values, Options);

    public static IReadOnlyList<string> ToStringList(string json) =>
        JsonSerializer.Deserialize<string[]>(json, Options) ?? [];

    public static string ToJson(IReadOnlyList<Technology> values) =>
        JsonSerializer.Serialize(values.Select(technology => technology.Name).ToArray(), Options);

    public static IReadOnlyList<Technology> ToTechnologyList(string json) =>
        (JsonSerializer.Deserialize<string[]>(json, Options) ?? [])
            .Select(Technology.Create)
            .ToArray();

    public static string ToJson(IReadOnlyList<Profile> values) =>
        JsonSerializer.Serialize(
            values.Select(profile => new ProfileJson(profile.Network, profile.Username, profile.Url?.Value)).ToArray(),
            Options);

    public static IReadOnlyList<Profile> ToProfileList(string json) =>
        (JsonSerializer.Deserialize<ProfileJson[]>(json, Options) ?? [])
            .Select(profile => new Profile(
                profile.Network,
                profile.Username,
                profile.Url is null ? null : Url.Create(profile.Url)))
            .ToArray();

    // Url is a Domain value object with a private constructor and a derived Uri member; it is
    // persisted as its raw string and revalidated on the way back.
    internal sealed record ProfileJson(string Network, string? Username, string? Url);
}

internal sealed class StringListConverter() : ValueConverter<IReadOnlyList<string>, string>(
    values => JsonListCodec.ToJson(values),
    json => JsonListCodec.ToStringList(json));

internal sealed class TechnologyListConverter() : ValueConverter<IReadOnlyList<Technology>, string>(
    values => JsonListCodec.ToJson(values),
    json => JsonListCodec.ToTechnologyList(json));

internal sealed class ProfileListConverter() : ValueConverter<IReadOnlyList<Profile>, string>(
    values => JsonListCodec.ToJson(values),
    json => JsonListCodec.ToProfileList(json));
