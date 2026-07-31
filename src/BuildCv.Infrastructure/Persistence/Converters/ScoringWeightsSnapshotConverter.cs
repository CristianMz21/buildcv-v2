using System.Text.Json;
using BuildCv.Domain.Scoring;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// The weights an analysis was scored under, stored as one JSON column rather than six.
//
// They are a SNAPSHOT: their only job is to explain a score that was already computed, so nothing
// filters or aggregates on an individual weight. Spreading them across six columns would widen every
// Analyses row for a value that is only ever read as a whole, and would let a future migration
// change one of them without the SchemaVersion that says the scoring model moved.
//
// Reading goes back through the factory, so a persisted set that no longer sums to 1.0 fails loudly
// instead of silently explaining a score with weights that could not have produced it.
internal sealed class ScoringWeightsSnapshotConverter() : ValueConverter<ScoringWeightsSnapshot, string>(
    weights => ToJson(weights),
    json => FromJson(json))
{
    public const int MaxLength = 256;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string ToJson(ScoringWeightsSnapshot weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        return JsonSerializer.Serialize(
            new WeightsJson(
                weights.Skills,
                weights.Experience,
                weights.Education,
                weights.Certifications,
                weights.Projects,
                weights.SchemaVersion),
            Options);
    }

    public static ScoringWeightsSnapshot FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var weights = JsonSerializer.Deserialize<WeightsJson>(json, Options)
            ?? throw new FormatException("Persisted scoring weights are not a well-formed JSON object.");

        return ScoringWeightsSnapshot.Create(
            weights.Skills,
            weights.Experience,
            weights.Education,
            weights.Certifications,
            weights.Projects,
            weights.SchemaVersion);
    }

    internal sealed record WeightsJson(
        double Skills,
        double Experience,
        double Education,
        double Certifications,
        double Projects,
        int SchemaVersion);
}
