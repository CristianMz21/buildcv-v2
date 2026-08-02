using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Configurations;

// The Analyses row itself is entirely PLAINTEXT: this table IS the analytics. Every column is a score,
// a weight or a timestamp — derived numbers about a match, holding no identifier of their own. The two
// ids are already opaque Guids, and reaching a person from a row means joining through Resumes, where
// the encryption is.
//
// Recommendations are the one split judgement, and the split is the point. A recommendation's
// STRUCTURE — which section, how urgent, which rule fired, how much it is worth — stays plaintext and
// carries the (Section, Priority) index, so "which advice do we give most often, and does it help"
// is answerable by grouping on Kind. That is a better answer than the free text ever gave: counting
// distinct sentences would have counted phrasings, not rules.
//
// The MESSAGE is sealed. It is not generic advice — it quotes the candidate's resume and the posting
// back at them ("your resume does not mention Kubernetes, which this role requires"), so a dump of
// this table would read as a summary of what every candidate on the platform is missing.
internal sealed class AnalysisConfiguration : IEntityTypeConfiguration<Analysis>
{
    private readonly IFieldEncryptor _encryptor;

    public AnalysisConfiguration(IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        _encryptor = encryptor;
    }

    public void Configure(EntityTypeBuilder<Analysis> builder)
    {
        builder.ToTable("Analyses", "scoring");

        builder.HasKey(analysis => analysis.Id).IsClustered(false);
        builder.Property(analysis => analysis.Id).HasColumnName("Id").ValueGeneratedNever();

        builder.HasAuditColumns();
        builder.HasSoftDelete();
        builder.HasRowVersion();
        builder.HasKeysetSequence();

        builder.Property(analysis => analysis.ResumeId).IsRequired();
        builder.Property(analysis => analysis.JobPostingId).IsRequired();
        builder.Property(analysis => analysis.ScoredAt).IsRequired();

        // "Score history for this resume", keyset paginated.
        builder.HasIndex(nameof(Analysis.ResumeId), ShadowColumns.Seq);

        // The time axis every rollup groups by.
        builder.HasIndex(analysis => analysis.ScoredAt);

        ConfigureBreakdown(builder);
        ConfigureRecommendations(builder);

        // Both are pure functions of Breakdown. A persisted Band would silently keep the old
        // classification the first time the band thresholds move.
        builder.Ignore(analysis => analysis.OverallScore);
        builder.Ignore(analysis => analysis.Band);
    }

    private static void ConfigureBreakdown(EntityTypeBuilder<Analysis> builder)
    {
        builder.OwnsOne(analysis => analysis.Breakdown, breakdown =>
        {
            breakdown.Property(scores => scores.SkillsScore).HasColumnName("SkillsScore").IsRequired();
            breakdown.Property(scores => scores.ExperienceScore).HasColumnName("ExperienceScore").IsRequired();
            breakdown.Property(scores => scores.EducationScore).HasColumnName("EducationScore").IsRequired();
            breakdown.Property(scores => scores.CertificationsScore).HasColumnName("CertificationsScore").IsRequired();
            breakdown.Property(scores => scores.ProjectsScore).HasColumnName("ProjectsScore").IsRequired();
            breakdown.Property(scores => scores.LanguagesScore).HasColumnName("LanguagesScore").IsRequired();

            // One column, not six: the weights are a snapshot read only as a whole, and keeping them
            // together with their SchemaVersion is what makes an old score explainable after the
            // scoring model changes.
            breakdown.Property(scores => scores.Weights)
                .HasColumnName("Weights")
                .HasConversion<ScoringWeightsSnapshotConverter>()
                .HasMaxLength(ScoringWeightsSnapshotConverter.MaxLength)
                .IsUnicode(false)
                .IsRequired();

            // Recomputable from the six scores and the weights in the same row. Storing it would
            // let the stored total and its own inputs disagree.
            breakdown.Ignore(scores => scores.WeightedTotal);

            // Also computed — it pairs those same six scores with the weights beside them. Leaving it
            // mapped does NOT produce six more columns: EF tries to discover SectionScore as a related
            // entity type and the whole model build throws, taking every model-shape test with it
            // rather than failing one assertion. Verified by removing this line.
            breakdown.Ignore(scores => scores.Sections);
        });

        builder.Navigation(analysis => analysis.Breakdown).IsRequired();
    }

    private void ConfigureRecommendations(EntityTypeBuilder<Analysis> builder)
    {
        builder.OwnsMany(analysis => analysis.Recommendations, recommendation =>
        {
            recommendation.ToTable("Recommendations", "scoring");
            recommendation.WithOwner().HasForeignKey(ChildTable.AnalysisForeignKey);
            recommendation.Property<int>(ChildTable.Key).ValueGeneratedOnAdd();
            recommendation.HasKey(ChildTable.Key);

            // PLAINTEXT, and this is the half that answers the analytics question. Kind in particular
            // names the rule that fired, which is what makes "which advice do we give most often"
            // a GROUP BY rather than a text-mining project.
            recommendation.Property(entry => entry.Section).HasColumnType("tinyint").IsRequired();
            recommendation.Property(entry => entry.Priority).HasColumnType("tinyint").IsRequired();
            recommendation.Property(entry => entry.Kind).HasColumnType("tinyint").IsRequired();
            recommendation.Property(entry => entry.Impact).IsRequired();

            // CONFIDENTIAL: the sentence quotes the resume and the posting it was scored against.
            recommendation.Property(entry => entry.Message)
                .IsRequired()
                .IsEncryptedText(_encryptor, "Recommendation.Message");

            recommendation.HasIndex(ChildTable.AnalysisForeignKey);

            // The rollup index. Never over Message: an index on an encrypted column enforces and
            // matches nothing, because every conversion produces a fresh nonce.
            recommendation.HasIndex(nameof(Recommendation.Section), nameof(Recommendation.Priority));
        });

        // No Rank column, deliberately. ChildTable explains why a positional key is rejected, and the
        // same argument applies to a stored position: this table is an honest SET, and the order the
        // advice is shown in is re-derived on read from Priority and Impact.
        builder.Navigation(analysis => analysis.Recommendations).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
