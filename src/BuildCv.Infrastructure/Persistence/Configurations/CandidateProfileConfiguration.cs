using BuildCv.Domain.Candidates;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Configurations;

// Everything a candidate has ever done, owned by the account rather than by any one CV.
//
// THE CLASSIFICATION IS NOT RESTATED HERE. The ten item collections are mapped by CvItemCollections,
// the same class ResumeConfiguration delegates to, so there is exactly one statement of which of those
// columns is sealed and why. Read that file before changing any of it; a copy of any of its reasoning
// here would be a second classification for one column, and the two would diverge on the first edit.
//
// What this file decides is what belongs to a PROFILE rather than to a CV: the schema, the foreign-key
// name, the one-per-account rule, and the AAD prefix that keeps these columns from sharing an envelope
// namespace with the resumes tables.
internal sealed class CandidateProfileConfiguration : IEntityTypeConfiguration<CandidateProfile>
{
    // The AAD prefix for every encrypted column under this root. It is what stops a ciphertext being
    // moved from resumes.Experiences.Organization into candidates.Experiences.Organization and still
    // decrypting — the same property SchemaRoundTripTests executes for the two recommendation tables.
    //
    // It costs nothing to copy an entry into a generated CV: that copy happens in memory, so the
    // converter decrypts under this context and re-encrypts under the resume's.
    private const string ContextPrefix = "CandidateProfile.";

    private const string Schema = "candidates";
    private const string ForeignKey = ChildTable.CandidateProfileForeignKey;

    private readonly IFieldEncryptor _encryptor;
    private readonly CvItemCollections _items;

    public CandidateProfileConfiguration(IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        _encryptor = encryptor;
        _items = new CvItemCollections(encryptor, ContextPrefix);
    }

    public void Configure(EntityTypeBuilder<CandidateProfile> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Profiles", Schema);

        builder.HasKey(profile => profile.Id).IsClustered(false);
        builder.Property(profile => profile.Id).HasColumnName("Id").ValueGeneratedNever();

        builder.HasAuditColumns();
        builder.HasSoftDelete();
        builder.HasRowVersion();

        // Carried even though nothing pages this table — there is one row per account and the port has
        // no list method on purpose. The OTHER job the sequence does is what earns it here: it is the
        // clustered index, so inserts land at the end of the B-tree instead of fragmenting every page
        // the way a random Guid clustered key would.
        builder.HasKeysetSequence();

        builder.Property(profile => profile.OwnerId).IsRequired();
        builder.Property(profile => profile.CreatedAt).IsRequired();
        builder.Property(profile => profile.UpdatedAt).IsRequired();

        // ONE PROFILE PER ACCOUNT, enforced by the database rather than by a check in a handler: the
        // aggregate is created lazily on first write, so two concurrent imports would otherwise both
        // read "no profile yet" and insert one, and the loser of that race is a whole second copy of
        // the candidate's history that nothing would ever reconcile.
        //
        // FILTERED on the tombstone, like every other unique index here, so deleting an account
        // genuinely frees the slot for a re-registration rather than leaving a row that blocks it.
        builder.HasIndex(nameof(CandidateProfile.OwnerId))
            .IsUnique()
            .HasFilter($"[{ShadowColumns.DeletedAt}] IS NULL");

        ConfigureContactInformation(builder);
        ConfigureItemCollections(builder);
    }

    // Every member is CONFIDENTIAL, exactly as on a resume and for the same reason: this is the block
    // that names the human. Sealed under this root's own prefix, so the envelope in Contact_FullName
    // here cannot be lifted into the resumes row of the same person and read there.
    private void ConfigureContactInformation(EntityTypeBuilder<CandidateProfile> builder)
    {
        builder.OwnsOne(profile => profile.ContactInformation, contact =>
        {
            contact.Property(information => information.FullName)
                .HasColumnName("Contact_FullName")
                .IsRequired()
                .IsEncryptedPersonName(_encryptor, ContextPrefix + "ContactInformation.FullName");

            contact.Property(information => information.Email)
                .HasColumnName("Contact_Email")
                .IsRequired()
                .IsEncryptedEmail(_encryptor, ContextPrefix + "ContactInformation.Email");

            contact.Property(information => information.PhoneNumber)
                .HasColumnName("Contact_PhoneNumber")
                .IsEncryptedPhoneNumber(_encryptor, ContextPrefix + "ContactInformation.PhoneNumber");

            contact.Property(information => information.Location)
                .HasColumnName("Contact_Location")
                .IsEncryptedText(_encryptor, ContextPrefix + "ContactInformation.Location");

            contact.Property(information => information.Website)
                .HasColumnName("Contact_Website")
                .IsEncryptedUrl(_encryptor, ContextPrefix + "ContactInformation.Website");

            contact.Property(information => information.Summary)
                .HasColumnName("Contact_Summary")
                .IsEncryptedText(_encryptor, ContextPrefix + "ContactInformation.Summary");

            // One envelope over the serialized list rather than a readable array of ciphertexts, whose
            // length alone would leak how many networks a candidate is on.
            contact.Property(information => information.Profiles)
                .HasColumnName("Contact_Profiles")
                .IsRequired()
                .IsEncryptedProfileList(_encryptor, ContextPrefix + "ContactInformation.Profiles");
        });

        builder.Navigation(profile => profile.ContactInformation).IsRequired();
    }

    // NO ImportSignals, and its absence is a decision rather than an omission. Those columns describe
    // the one PDF a resume was imported from; a profile is fed from several documents and from fields
    // typed by hand, so "the document this came from" has no single answer here. The readability engine
    // reads them off a resume, which is the only place they mean anything.
    private void ConfigureItemCollections(EntityTypeBuilder<CandidateProfile> builder)
    {
        builder.OwnsMany(profile => profile.Experiences, entries => _items.Experiences(entries, Schema, ForeignKey));
        builder.OwnsMany(profile => profile.Educations, entries => _items.Educations(entries, Schema, ForeignKey));
        builder.OwnsMany(profile => profile.Skills, entries => CvItemCollections.Skills(entries, Schema, ForeignKey));
        builder.OwnsMany(profile => profile.Projects, entries => _items.Projects(entries, Schema, ForeignKey));
        builder.OwnsMany(profile => profile.Certificates, entries => _items.Certificates(entries, Schema, ForeignKey));
        builder.OwnsMany(profile => profile.Languages, entries => _items.Languages(entries, Schema, ForeignKey));
        builder.OwnsMany(profile => profile.Awards, entries => _items.Awards(entries, Schema, ForeignKey));
        builder.OwnsMany(profile => profile.Publications, entries => _items.Publications(entries, Schema, ForeignKey));
        builder.OwnsMany(profile => profile.Interests, entries => _items.Interests(entries, Schema, ForeignKey));
        builder.OwnsMany(profile => profile.References, entries => _items.References(entries, Schema, ForeignKey));

        UseBackingField(builder, profile => profile.Experiences);
        UseBackingField(builder, profile => profile.Educations);
        UseBackingField(builder, profile => profile.Skills);
        UseBackingField(builder, profile => profile.Projects);
        UseBackingField(builder, profile => profile.Certificates);
        UseBackingField(builder, profile => profile.Languages);
        UseBackingField(builder, profile => profile.Awards);
        UseBackingField(builder, profile => profile.Publications);
        UseBackingField(builder, profile => profile.Interests);
        UseBackingField(builder, profile => profile.References);
    }

    // Each getter returns _entries.AsReadOnly(), so reading through the property hands EF a
    // ReadOnlyCollection wrapper it cannot add to; the change tracker needs the List behind it.
    private static void UseBackingField<TEntry>(
        EntityTypeBuilder<CandidateProfile> builder,
        System.Linq.Expressions.Expression<Func<CandidateProfile, IEnumerable<TEntry>?>> navigation)
        where TEntry : class =>
        builder.Navigation(navigation).UsePropertyAccessMode(PropertyAccessMode.Field);
}
