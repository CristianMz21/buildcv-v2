using System.Text.Json;
using BuildCv.Domain.Readability;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// The weights a readability report was evaluated under, stored as one JSON column rather than five.
//
// The same argument ScoringWeightsSnapshotConverter makes, and it holds for the same reason: they are a
// SNAPSHOT whose only job is to explain a report that was already computed, so nothing filters or
// aggregates on an individual weight. Spreading them across five columns would widen every row for a
// value that is only ever read as a whole, and would let a future migration change one of them without
// the SchemaVersion that says the readability model moved.
//
// Reading goes back through the factory, so a persisted set that no longer sums to 1.0 fails loudly
// instead of silently explaining a report with weights that could not have produced it.
internal sealed class ReadabilityWeightsSnapshotConverter() : ValueConverter<ReadabilityWeightsSnapshot, string>(
    weights => ToJson(weights),
    json => FromJson(json))
{
    public const int MaxLength = 256;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string ToJson(ReadabilityWeightsSnapshot weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        return JsonSerializer.Serialize(
            new WeightsJson(
                weights.Completeness,
                weights.Contact,
                weights.Achievements,
                weights.Chronology,
                weights.AtsParseability,
                weights.SchemaVersion),
            Options);
    }

    public static ReadabilityWeightsSnapshot FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var weights = JsonSerializer.Deserialize<WeightsJson>(json, Options)
            ?? throw new FormatException("Persisted readability weights are not a well-formed JSON object.");

        return ReadabilityWeightsSnapshot.Create(
            weights.Completeness,
            weights.Contact,
            weights.Achievements,
            weights.Chronology,
            weights.AtsParseability,
            // The PERSISTED version, never the current one. This argument looks redundant and is not:
            // the parameter is optional, so deleting it compiles and every historical row silently
            // starts reporting whatever version ships today -- the exact failure the field exists to
            // prevent.
            weights.SchemaVersion);
    }

    internal sealed record WeightsJson(
        double Completeness,
        double Contact,
        double Achievements,
        double Chronology,
        double AtsParseability,
        int SchemaVersion);
}
