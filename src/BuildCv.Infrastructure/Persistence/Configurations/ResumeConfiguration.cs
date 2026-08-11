using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Configurations;

// The resume is where the product's data classification lives or dies.
//
// The rule applied throughout: a value that IDENTIFIES OR DESCRIBES A PERSON is encrypted; a value
// that describes a SKILL, A LEVEL OR A SPAN OF TIME is plaintext. So "who you worked for" and "what
// you wrote about yourself" go into envelopes, while "C#", "Advanced", "4 years" and
// "2020-01-01/2023-06-30" stay queryable — those are the columns the scoring engine and the internal
// analytics read, and no query can reach through an envelope.
//
// The split is not cosmetic. Put Skill.Name behind encryption and skill-gap analytics becomes
// impossible; leave Experience.Organization in plaintext and a database dump tells an attacker where
// every candidate on the platform works.
//
// WHAT THIS FILE STILL DECIDES IS THE TWO OWNED REFERENCES BELOW — ContactInformation and
// ImportSignals — because those belong to a resume and to nothing else. THE TEN ITEM COLLECTIONS ARE
// NOT CLASSIFIED HERE: they live in CvItemCollections, shared with CandidateProfile, which owns the
// same ten types. Read that file for the per-column reasoning, and change it there; a copy of any of
// it here would be a second classification for one column, and the half that kept the plaintext is the
// half nobody would notice.
internal sealed class ResumeConfiguration : IEntityTypeConfiguration<Resume>
{
    private readonly IFieldEncryptor _encryptor;
    private readonly CvItemCollections _items;

    public ResumeConfiguration(IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        _encryptor = encryptor;
        _items = new CvItemCollections(encryptor);
    }

    public void Configure(EntityTypeBuilder<Resume> builder)
    {
        builder.ToTable("Resumes", "resumes");

        builder.HasKey(resume => resume.Id).IsClustered(false);
        builder.Property(resume => resume.Id).HasColumnName("Id").ValueGeneratedNever();

        builder.HasAuditColumns();
        builder.HasSoftDelete();
        builder.HasRowVersion();
        builder.HasKeysetSequence();

        builder.Property(resume => resume.OwnerId).IsRequired();

        // CONFIDENTIAL. It is free text a candidate writes about their own job search — "CV para la
        // entrevista en Globant" names an employer they may have told nobody — and it passes the test
        // this repository actually applies: nothing queries it. No engine reads it, no index needs it,
        // and no analytics groups by it, so sealing it costs no query.
        //
        // No HasMaxLength: an encrypted column is varbinary(max) and a length here would bound the
        // CIPHERTEXT rather than the text. The 120-character rule lives in the Domain, where it is
        // product policy rather than a truncation guard.
        builder.Property(resume => resume.Name)
            .IsEncryptedText(_encryptor, "Resume.Name");
        builder.Property(resume => resume.CreatedAt).IsRequired();
        builder.Property(resume => resume.UpdatedAt).IsRequired();

        // "My resumes, newest first" is the only list query on this table, and it is keyset
        // paginated. Descending on Seq so the index is walked in the direction it is read.
        builder.HasIndex(nameof(Resume.OwnerId), ShadowColumns.Seq)
            .IsDescending(false, true);

        ConfigureContactInformation(builder);
        ConfigureImportSignals(builder);
        ConfigureItemCollections(builder);
    }

    // Every member is CONFIDENTIAL. This is the block that names the human: full name, address,
    // phone, personal site, the summary they wrote about themselves, and their social handles. There
    // is no analytical use for any of it, so nothing is traded away by sealing all of it.
    //
    // Owned reference, so it lives in the Resumes row as Contact_* columns rather than a join.
    private void ConfigureContactInformation(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsOne(resume => resume.ContactInformation, contact =>
        {
            contact.Property(information => information.FullName)
                .HasColumnName("Contact_FullName")
                .IsRequired()
                .IsEncryptedPersonName(_encryptor, "Resume.ContactInformation.FullName");

            contact.Property(information => information.Email)
                .HasColumnName("Contact_Email")
                .IsRequired()
                .IsEncryptedEmail(_encryptor, "Resume.ContactInformation.Email");

            contact.Property(information => information.PhoneNumber)
                .HasColumnName("Contact_PhoneNumber")
                .IsEncryptedPhoneNumber(_encryptor, "Resume.ContactInformation.PhoneNumber");

            contact.Property(information => information.Location)
                .HasColumnName("Contact_Location")
                .IsEncryptedText(_encryptor, "Resume.ContactInformation.Location");

            contact.Property(information => information.Website)
                .HasColumnName("Contact_Website")
                .IsEncryptedUrl(_encryptor, "Resume.ContactInformation.Website");

            contact.Property(information => information.Summary)
                .HasColumnName("Contact_Summary")
                .IsEncryptedText(_encryptor, "Resume.ContactInformation.Summary");

            // Serialized to JSON and then sealed, so the column is one envelope rather than a
            // readable array of ciphertexts whose length alone would leak how many networks a
            // candidate is on.
            contact.Property(information => information.Profiles)
                .HasColumnName("Contact_Profiles")
                .IsRequired()
                .IsEncryptedProfileList(_encryptor, "Resume.ContactInformation.Profiles");
        });

        builder.Navigation(resume => resume.ContactInformation).IsRequired();
    }

    // EVERY MEMBER IS PLAINTEXT, and this is the block where that is easiest to get wrong by reflex —
    // it describes a file the candidate uploaded, so it FEELS like their data. It is not: two closed
    // enums, a bool and a page count, none of which can hold a fragment of the document. The
    // classification rule this file states is "a value that identifies or describes a PERSON is
    // encrypted"; a column layout describes a PDF.
    //
    // Sealing any of it would also end the only thing the columns are for. The readability engine reads
    // them on every run, and "how many candidates upload two-column PDFs" is the question that decides
    // whether the advice is working — neither survives an envelope, and no index can either.
    //
    // OPTIONAL owned reference, so it lives in the Resumes row as Import_* columns and every column is
    // nullable. Null across all four is what EF materializes back as a null ImportSignals, which is the
    // ordinary case: a resume typed by hand has no document to describe. It also makes the migration
    // additive — every row already on disk reads back as null and is renormalized out of its readability
    // report, rather than being given a fabricated default that would claim a document it never had.
    //
    // IT IS ALSO WHY ImportSignals IS NOT ONE OF THE SHARED ITEM COLLECTIONS. A profile is fed from
    // several documents and from fields typed by hand, so "the document this came from" has no single
    // answer there; on a resume it has exactly one, which is what the readability engine reads.
    private static void ConfigureImportSignals(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsOne(resume => resume.ImportSignals, signals =>
        {
            // tinyint, matching every other closed enum in this model. ImportSignals.Create refuses an
            // undefined member, which is what keeps the unchecked conversion from writing a durable
            // value that is a member of neither the enum nor the column's intent.
            signals.Property(value => value.ColumnLayout)
                .HasColumnName("Import_ColumnLayout")
                .HasColumnType("tinyint");

            signals.Property(value => value.HadTextLayer)
                .HasColumnName("Import_HadTextLayer");

            signals.Property(value => value.PageCount)
                .HasColumnName("Import_PageCount");

            // int, not tinyint: it is a bit field with room to grow, and a [Flags] enum that outgrew its
            // column would start truncating combinations rather than failing.
            signals.Property(value => value.Warnings)
                .HasColumnName("Import_Warnings");
        });

        builder.Navigation(resume => resume.ImportSignals).IsRequired(false);
    }

    // The ten collections, mapped by the shared class so that this aggregate and CandidateProfile
    // cannot disagree about a single column. Everything a resume adds on top of that shared shape is
    // right here: the schema, the foreign-key name, and the backing-field access mode.
    private void ConfigureItemCollections(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.Experiences, entries => _items.Experiences(entries, Schema, ForeignKey));
        builder.OwnsMany(resume => resume.Educations, entries => _items.Educations(entries, Schema, ForeignKey));
        builder.OwnsMany(resume => resume.Skills, entries => CvItemCollections.Skills(entries, Schema, ForeignKey));
        builder.OwnsMany(resume => resume.Projects, entries => _items.Projects(entries, Schema, ForeignKey));
        builder.OwnsMany(resume => resume.Certificates, entries => _items.Certificates(entries, Schema, ForeignKey));
        builder.OwnsMany(resume => resume.Languages, entries => _items.Languages(entries, Schema, ForeignKey));
        builder.OwnsMany(resume => resume.Awards, entries => _items.Awards(entries, Schema, ForeignKey));
        builder.OwnsMany(resume => resume.Publications, entries => _items.Publications(entries, Schema, ForeignKey));
        builder.OwnsMany(resume => resume.Interests, entries => _items.Interests(entries, Schema, ForeignKey));
        builder.OwnsMany(resume => resume.References, entries => _items.References(entries, Schema, ForeignKey));

        UseBackingField(builder, resume => resume.Experiences);
        UseBackingField(builder, resume => resume.Educations);
        UseBackingField(builder, resume => resume.Skills);
        UseBackingField(builder, resume => resume.Projects);
        UseBackingField(builder, resume => resume.Certificates);
        UseBackingField(builder, resume => resume.Languages);
        UseBackingField(builder, resume => resume.Awards);
        UseBackingField(builder, resume => resume.Publications);
        UseBackingField(builder, resume => resume.Interests);
        UseBackingField(builder, resume => resume.References);
    }

    private const string Schema = "resumes";
    private const string ForeignKey = ChildTable.ResumeForeignKey;

    // The one thing that must not be forgotten on any of the ten collections. Each getter returns
    // _entries.AsReadOnly(), so reading through the property hands EF a ReadOnlyCollection wrapper
    // it cannot add to; the change tracker needs the List behind it.
    private static void UseBackingField<TEntry>(
        EntityTypeBuilder<Resume> builder,
        System.Linq.Expressions.Expression<Func<Resume, IEnumerable<TEntry>?>> navigation)
        where TEntry : class =>
        builder.Navigation(navigation).UsePropertyAccessMode(PropertyAccessMode.Field);
}
