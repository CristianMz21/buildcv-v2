using BuildCv.Domain.Organizations;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Configurations;

// Organizations are entirely PLAINTEXT. A company name and its slug are published identifiers, not
// personal data — a recruiter's whole point is being findable. Encrypting them would break the slug
// lookup and the "postings per company" rollups for no privacy gain.
//
// The encryptor is still taken, so every configuration has the same shape and adding an encrypted
// column here later is a one-line change rather than a constructor change plus a DbContext change.
internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public OrganizationConfiguration(IFieldEncryptor encryptor) => ArgumentNullException.ThrowIfNull(encryptor);

    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations", "orgs");

        builder.HasKey(organization => organization.Id).IsClustered(false);
        builder.Property(organization => organization.Id).HasColumnName("Id").ValueGeneratedNever();

        builder.HasAuditColumns();
        builder.HasSoftDelete();
        builder.HasRowVersion();
        builder.HasKeysetSequence();

        builder.Property(organization => organization.Name)
            .HasConversion<OrganizationNameConverter>()
            .HasMaxLength(OrganizationNameConverter.MaxLength)
            .IsRequired();

        // Slug conversion comes from ConfigureConventions; only the constraint is stated here.
        // Filtered on DeletedAt so a deleted organization releases its slug — otherwise a tombstone
        // holds a public URL hostage with nothing in the API able to explain why.
        builder.Property(organization => organization.Slug).IsRequired();
        builder.HasIndex(organization => organization.Slug)
            .IsUnique()
            .HasFilter($"[{ShadowColumns.DeletedAt}] IS NULL");

        builder.Property(organization => organization.Status)
            .HasColumnType("tinyint")
            .IsRequired();

        builder.Property(organization => organization.CreatedAt).IsRequired();
        builder.Property(organization => organization.UpdatedAt).IsRequired();

        builder.OwnsMany(organization => organization.Members, member =>
        {
            member.ToTable("Memberships", "orgs");
            member.WithOwner().HasForeignKey(ChildTable.OrganizationForeignKey);
            member.Property<int>(ChildTable.Key).ValueGeneratedOnAdd();
            member.HasKey(ChildTable.Key);

            member.Property(membership => membership.AccountId).IsRequired();
            member.Property(membership => membership.Role).HasColumnType("tinyint").IsRequired();
            member.Property(membership => membership.JoinedAt).IsRequired();

            // Organization.AddMember already refuses a duplicate; this is the same rule stated where
            // a concurrent second request cannot slip past it, since two transactions can both pass
            // the in-memory check before either commits.
            member.HasIndex(ChildTable.OrganizationForeignKey, nameof(Membership.AccountId)).IsUnique();
        });

        // Field access, not the property: the getter hands back _members.AsReadOnly(), and EF cannot
        // add to a ReadOnlyCollection wrapper.
        builder.Navigation(organization => organization.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
