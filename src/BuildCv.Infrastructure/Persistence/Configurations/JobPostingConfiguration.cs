using BuildCv.Domain.Jobs;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Configurations;

// Job postings are entirely PLAINTEXT, and that is the point of them. A posting is published to the
// world; its title, description and required skills are the corpus every match score is computed
// against, and "which skills are most demanded this quarter" is a first-class internal question.
// None of it is personal data about the candidate the product protects.
internal sealed class JobPostingConfiguration : IEntityTypeConfiguration<JobPosting>
{
    private const int TitleMaxLength = 200;
    private const int DescriptionMaxLength = 5000;

    public JobPostingConfiguration(IFieldEncryptor encryptor) => ArgumentNullException.ThrowIfNull(encryptor);

    public void Configure(EntityTypeBuilder<JobPosting> builder)
    {
        builder.ToTable("JobPostings", "jobs");

        builder.HasKey(posting => posting.Id).IsClustered(false);
        builder.Property(posting => posting.Id).HasColumnName("Id").ValueGeneratedNever();

        builder.HasAuditColumns();
        builder.HasSoftDelete();
        builder.HasRowVersion();
        builder.HasKeysetSequence();

        builder.Property(posting => posting.OwnerId).IsRequired();

        builder.Property(posting => posting.Title)
            .HasMaxLength(TitleMaxLength)
            .IsRequired();

        builder.Property(posting => posting.Description)
            .HasMaxLength(DescriptionMaxLength);

        // Exactly one of the two is set: CompanyId when the posting belongs to a registered
        // organization, CompanyName when the poster only typed a company in. Both stay nullable
        // because the Domain has two factories, one per case.
        builder.Property(posting => posting.CompanyId);

        builder.Property(posting => posting.CompanyName)
            .HasConversion<OrganizationNameConverter>()
            .HasMaxLength(OrganizationNameConverter.MaxLength);

        builder.Property(posting => posting.Status)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(posting => posting.CreatedAt).IsRequired();
        builder.Property(posting => posting.UpdatedAt).IsRequired();
        builder.Property(posting => posting.PublishedAt);
        builder.Property(posting => posting.ClosesAt);

        // Seq rather than CreatedAt as the second column: it is the tiebreaker that makes keyset
        // pagination over "my postings" and "this company's postings" total.
        builder.HasIndex(nameof(JobPosting.OwnerId), ShadowColumns.Seq);
        builder.HasIndex(nameof(JobPosting.CompanyId), ShadowColumns.Seq);
        builder.HasIndex(posting => posting.Status);

        builder.OwnsMany(posting => posting.Requirements, requirement =>
        {
            requirement.ToTable("JobRequirements", "jobs");
            requirement.WithOwner().HasForeignKey(ChildTable.JobPostingForeignKey);
            requirement.Property<int>(ChildTable.Key).ValueGeneratedOnAdd();
            requirement.HasKey(ChildTable.Key);

            // ANALYTICAL. The whole scoring engine reads this column; encrypting it would end the
            // product.
            requirement.Property(jobRequirement => jobRequirement.Skill).IsRequired();
            requirement.Property(jobRequirement => jobRequirement.Priority).HasColumnType("tinyint").IsRequired();
            requirement.Property(jobRequirement => jobRequirement.Weight).IsRequired();

            requirement.HasIndex(nameof(JobRequirement.Skill));
        });

        builder.Navigation(posting => posting.Requirements).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(posting => posting.Responsibilities, responsibility =>
        {
            responsibility.ToTable("Responsibilities", "jobs");
            responsibility.WithOwner().HasForeignKey(ChildTable.JobPostingForeignKey);
            responsibility.Property<int>(ChildTable.Key).ValueGeneratedOnAdd();
            responsibility.HasKey(ChildTable.Key);

            responsibility.Property(item => item.Description).HasMaxLength(500).IsRequired();
        });

        builder.Navigation(posting => posting.Responsibilities).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
