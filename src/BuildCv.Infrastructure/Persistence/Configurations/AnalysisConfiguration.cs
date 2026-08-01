using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Configurations;

// Analyses are entirely PLAINTEXT: this table IS the analytics. Every column is a score, a weight or
// a timestamp — derived numbers about a match, holding no identifier of their own. The two ids are
// already opaque Guids, and reaching a person from a row means joining through Resumes, where the
// encryption is.
//
// The recommendations are the one judgement call. They are generated advice about a resume, not
// content the candidate wrote, and they are pure text about the gap between a resume and a posting;
// they stay plaintext so a future "which advice do we give most often, and does it help" question
// stays answerable.
internal sealed class AnalysisConfiguration : IEntityTypeConfiguration<Analysis>
{
    public AnalysisConfiguration(IFieldEncryptor encryptor) => ArgumentNullException.ThrowIfNull(encryptor);

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

        builder.Property(analysis => analysis.Recommendations)
            .HasConversion<StringListConverter>(ConvertedComparers.ForList<string>())
            .IsRequired();

        // "Score history for this resume", keyset paginated.
        builder.HasIndex(nameof(Analysis.ResumeId), ShadowColumns.Seq);

        // The time axis every rollup groups by.
        builder.HasIndex(analysis => analysis.ScoredAt);

        builder.OwnsOne(analysis => analysis.Breakdown, breakdown =>
        {
            breakdown.Property(scores => scores.SkillsScore).HasColumnName("SkillsScore").IsRequired();
            breakdown.Property(scores => scores.ExperienceScore).HasColumnName("ExperienceScore").IsRequired();
            breakdown.Property(scores => scores.EducationScore).HasColumnName("EducationScore").IsRequired();
            breakdown.Property(scores => scores.CertificationsScore).HasColumnName("CertificationsScore").IsRequired();
            breakdown.Property(scores => scores.ProjectsScore).HasColumnName("ProjectsScore").IsRequired();

            // One column, not six: the weights are a snapshot read only as a whole, and keeping them
            // together with their SchemaVersion is what makes an old score explainable after the
            // scoring model changes.
            breakdown.Property(scores => scores.Weights)
                .HasColumnName("Weights")
                .HasConversion<ScoringWeightsSnapshotConverter>()
                .HasMaxLength(ScoringWeightsSnapshotConverter.MaxLength)
                .IsUnicode(false)
                .IsRequired();

            // Recomputable from the five scores and the weights in the same row. Storing it would
            // let the stored total and its own inputs disagree.
            breakdown.Ignore(scores => scores.WeightedTotal);
        });

        builder.Navigation(analysis => analysis.Breakdown).IsRequired();

        // Both are pure functions of Breakdown. A persisted Band would silently keep the old
        // classification the first time the band thresholds move.
        builder.Ignore(analysis => analysis.OverallScore);
        builder.Ignore(analysis => analysis.Band);
    }
}
