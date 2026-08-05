using BuildCv.Domain.Readability;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Configurations;

// The Reports row itself is entirely PLAINTEXT, on the same argument AnalysisConfiguration makes: every
// column is a score, a weight or a timestamp — derived numbers about a document, holding no identifier
// of their own. The one id is already an opaque Guid, and reaching a person from a row means joining
// through Resumes, where the encryption is.
//
// Recommendations are the one split judgement, and the split is copied deliberately rather than
// re-decided. A recommendation's STRUCTURE — which section, how urgent, which rule fired, how much it is
// worth — stays plaintext and carries the (Section, Priority) index, so "which advice do we give most
// often, and does it help" is answerable by grouping on Kind.
//
// The MESSAGE is sealed, and here it is if anything MORE necessary than on the scoring side: readability
// advice quotes the candidate's own bullet points and job titles back at them ("Add an entry covering
// the 14-month gap before 'Backend Developer'"), so a dump of this table would read as a summary of
// every gap in every candidate's history on the platform.
internal sealed class ReadabilityReportConfiguration : IEntityTypeConfiguration<ReadabilityReport>
{
    private readonly IFieldEncryptor _encryptor;

    public ReadabilityReportConfiguration(IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        _encryptor = encryptor;
    }

    public void Configure(EntityTypeBuilder<ReadabilityReport> builder)
    {
        builder.ToTable("Reports", "readability");

        builder.HasKey(report => report.Id).IsClustered(false);
        builder.Property(report => report.Id).HasColumnName("Id").ValueGeneratedNever();

        builder.HasAuditColumns();
        builder.HasSoftDelete();
        builder.HasRowVersion();
        builder.HasKeysetSequence();

        builder.Property(report => report.ResumeId).IsRequired();
        builder.Property(report => report.EvaluatedAt).IsRequired();

        // "Readability history for this resume", keyset paginated. The index is created with the table
        // rather than with the endpoint that will read it: a covering index added later is a migration
        // against a table that already has rows, and this one costs a single index on inserts nobody is
        // making at volume yet.
        builder.HasIndex(nameof(ReadabilityReport.ResumeId), ShadowColumns.Seq);

        // The time axis any rollup would group by.
        builder.HasIndex(report => report.EvaluatedAt);

        ConfigureBreakdown(builder);
        ConfigureRecommendations(builder);

        // Both are pure functions of Breakdown. A persisted Band would silently keep the old
        // classification the first time the band thresholds move.
        builder.Ignore(report => report.ReadabilityScore);
        builder.Ignore(report => report.Band);
    }

    private static void ConfigureBreakdown(EntityTypeBuilder<ReadabilityReport> builder)
    {
        builder.OwnsOne(report => report.Breakdown, breakdown =>
        {
            breakdown.Property(scores => scores.CompletenessScore).HasColumnName("CompletenessScore").IsRequired();
            breakdown.Property(scores => scores.ContactScore).HasColumnName("ContactScore").IsRequired();
            breakdown.Property(scores => scores.AchievementsScore).HasColumnName("AchievementsScore").IsRequired();
            breakdown.Property(scores => scores.ChronologyScore).HasColumnName("ChronologyScore").IsRequired();
            breakdown.Property(scores => scores.AtsParseabilityScore)
                .HasColumnName("AtsParseabilityScore").IsRequired();

            // One column, not five: the weights are a snapshot read only as a whole, and keeping them
            // together with their SchemaVersion is what makes an old report explainable after the
            // readability model changes.
            breakdown.Property(scores => scores.Weights)
                .HasColumnName("Weights")
                .HasConversion<ReadabilityWeightsSnapshotConverter>()
                .HasMaxLength(ReadabilityWeightsSnapshotConverter.MaxLength)
                .IsUnicode(false)
                .IsRequired();

            // Recomputable from the five scores and the weights in the same row. Storing it would let
            // the stored total and its own inputs disagree.
            breakdown.Ignore(scores => scores.WeightedTotal);

            // Also computed — it pairs those same five scores with the weights beside them. Leaving it
            // mapped does NOT produce five more columns: EF tries to discover ReadabilitySectionScore as
            // a related entity type and the whole model build throws, taking every model-shape test with
            // it rather than failing one assertion.
            breakdown.Ignore(scores => scores.Sections);
        });

        builder.Navigation(report => report.Breakdown).IsRequired();
    }

    private void ConfigureRecommendations(EntityTypeBuilder<ReadabilityReport> builder)
    {
        builder.OwnsMany(report => report.Recommendations, recommendation =>
        {
            recommendation.ToTable("Recommendations", "readability");
            recommendation.WithOwner().HasForeignKey(ChildTable.ReadabilityReportForeignKey);
            recommendation.Property<int>(ChildTable.Key).ValueGeneratedOnAdd();
            recommendation.HasKey(ChildTable.Key);

            // PLAINTEXT, and this is the half that answers the analytics question. Kind in particular
            // names the rule that fired, which is what makes "which advice do we give most often" a
            // GROUP BY rather than a text-mining project.
            recommendation.Property(entry => entry.Section).HasColumnType("tinyint").IsRequired();
            recommendation.Property(entry => entry.Priority).HasColumnType("tinyint").IsRequired();
            recommendation.Property(entry => entry.Kind).HasColumnType("tinyint").IsRequired();
            recommendation.Property(entry => entry.Impact).IsRequired();

            // CONFIDENTIAL, under a context string OF ITS OWN. "Recommendation.Message" already belongs
            // to scoring.Recommendations, and the context is the AAD: sharing one would let an envelope
            // be moved between the two tables and still decrypt, which is exactly what binding it to the
            // column is for. ModelConfigurationTests asserts the uniqueness in both directions.
            recommendation.Property(entry => entry.Message)
                .IsRequired()
                .IsEncryptedText(_encryptor, "ReadabilityRecommendation.Message");

            recommendation.HasIndex(ChildTable.ReadabilityReportForeignKey);

            // The rollup index. Never over Message: an index on an encrypted column enforces and matches
            // nothing, because every conversion produces a fresh nonce.
            recommendation.HasIndex(
                nameof(ReadabilityRecommendation.Section), nameof(ReadabilityRecommendation.Priority));
        });

        // No Rank column, deliberately. ChildTable explains why a positional key is rejected, and the
        // same argument applies to a stored position: this table is an honest SET, and the order the
        // advice is shown in is re-derived on read from Priority and Impact.
        builder.Navigation(report => report.Recommendations).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
