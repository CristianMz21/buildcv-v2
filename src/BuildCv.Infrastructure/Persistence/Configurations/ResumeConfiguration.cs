using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Persistence.Converters;
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
internal sealed class ResumeConfiguration : IEntityTypeConfiguration<Resume>
{
    private readonly IFieldEncryptor _encryptor;

    public ResumeConfiguration(IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        _encryptor = encryptor;
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
        builder.Property(resume => resume.CreatedAt).IsRequired();
        builder.Property(resume => resume.UpdatedAt).IsRequired();

        // "My resumes, newest first" is the only list query on this table, and it is keyset
        // paginated. Descending on Seq so the index is walked in the direction it is read.
        builder.HasIndex(nameof(Resume.OwnerId), ShadowColumns.Seq)
            .IsDescending(false, true);

        ConfigureContactInformation(builder);
        ConfigureExperiences(builder);
        ConfigureEducations(builder);
        ConfigureSkills(builder);
        ConfigureProjects(builder);
        ConfigureCertificates(builder);
        ConfigureLanguages(builder);
        ConfigureAwards(builder);
        ConfigurePublications(builder);
        ConfigureInterests(builder);
        ConfigureReferences(builder);
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

    private void ConfigureExperiences(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.Experiences, experience =>
        {
            ChildTableOf(experience, "Experiences");

            // PLAINTEXT: professional vs volunteer is a category, not an identity.
            experience.Property(entry => entry.Type).HasColumnType("tinyint").IsRequired();

            // CONFIDENTIAL: the employer and the role name together identify a person more reliably
            // than most direct identifiers do.
            experience.Property(entry => entry.Organization)
                .IsRequired()
                .IsEncryptedOrganizationName(_encryptor, "Experience.Organization");

            experience.Property(entry => entry.Position)
                .IsRequired()
                .IsEncryptedText(_encryptor, "Experience.Position");

            experience.Property(entry => entry.Summary)
                .IsEncryptedText(_encryptor, "Experience.Summary");

            experience.Property(entry => entry.Highlights)
                .IsRequired()
                .IsEncryptedStringList(_encryptor, "Experience.Highlights");

            // PLAINTEXT: tenure length is what "years of experience" scoring is computed from.
            experience.Property(entry => entry.Period).IsRequired();
        });

        UseBackingField(builder, resume => resume.Experiences);
    }

    private void ConfigureEducations(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.Educations, education =>
        {
            ChildTableOf(education, "Educations");

            // CONFIDENTIAL: school, degree and grade are re-identifying, and grade in particular is
            // the kind of thing a candidate would never expect to be readable in a dump.
            education.Property(entry => entry.Institution)
                .IsRequired()
                .IsEncryptedOrganizationName(_encryptor, "Education.Institution");

            education.Property(entry => entry.Degree)
                .IsEncryptedText(_encryptor, "Education.Degree");

            education.Property(entry => entry.FieldOfStudy)
                .IsEncryptedText(_encryptor, "Education.FieldOfStudy");

            education.Property(entry => entry.Grade)
                .IsEncryptedText(_encryptor, "Education.Grade");

            education.Property(entry => entry.Period).IsRequired();

            // PLAINTEXT, joining Period as the second column here that is. A rung on a ladder is not
            // a description of a person: it is what the engine compares against a posting's required
            // level, and it could not do that from behind the envelope Degree sits in.
            education.Property(entry => entry.Level).HasColumnType("tinyint");
        });

        UseBackingField(builder, resume => resume.Educations);
    }

    // Wholly PLAINTEXT, and deliberately so. "Which skills at which level for how long" is the
    // corpus every match score is computed against and the only table internal analytics can group
    // by. A skill name is a fact about a technology, not about a person.
    private static void ConfigureSkills(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.Skills, skill =>
        {
            ChildTableOf(skill, "Skills");

            skill.Property(entry => entry.Name).IsRequired();
            skill.Property(entry => entry.Level).HasColumnType("tinyint");
            skill.Property(entry => entry.YearsOfExperience);

            skill.Property(entry => entry.Keywords)
                .HasConversion<StringListConverter>(ConvertedComparers.ForList<string>())
                .IsRequired();

            // The join every scoring run makes: find resumes whose skills match a posting's
            // requirements.
            skill.HasIndex(nameof(Skill.Name));
        });

        UseBackingField(builder, resume => resume.Skills);
    }

    private void ConfigureProjects(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.Projects, project =>
        {
            ChildTableOf(project, "Projects");

            // CONFIDENTIAL: a project name plus its repository URL is a direct link to a named GitHub
            // account. The description and highlights are free text the candidate wrote.
            project.Property(entry => entry.Name)
                .IsRequired()
                .IsEncryptedText(_encryptor, "Project.Name");

            project.Property(entry => entry.Description)
                .IsEncryptedText(_encryptor, "Project.Description");

            project.Property(entry => entry.RepositoryUrl)
                .IsEncryptedUrl(_encryptor, "Project.RepositoryUrl");

            project.Property(entry => entry.LiveDemoUrl)
                .IsEncryptedUrl(_encryptor, "Project.LiveDemoUrl");

            project.Property(entry => entry.Highlights)
                .IsRequired()
                .IsEncryptedStringList(_encryptor, "Project.Highlights");

            // PLAINTEXT: the technology list is scoring input, exactly like Skills. It says what was
            // used, not who used it.
            project.Property(entry => entry.Technologies)
                .HasConversion<TechnologyListConverter>(ConvertedComparers.ForList<Domain.Common.ValueObjects.Technology>())
                .IsRequired();

            project.Property(entry => entry.Period).IsRequired();
        });

        UseBackingField(builder, resume => resume.Projects);
    }

    private void ConfigureCertificates(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.Certificates, certificate =>
        {
            ChildTableOf(certificate, "Certificates");

            // CONFIDENTIAL: a credential id and its verification URL resolve to a named person on
            // the issuer's site.
            certificate.Property(entry => entry.Name)
                .IsRequired()
                .IsEncryptedText(_encryptor, "Certificate.Name");

            certificate.Property(entry => entry.Issuer)
                .IsRequired()
                .IsEncryptedOrganizationName(_encryptor, "Certificate.Issuer");

            certificate.Property(entry => entry.CredentialId)
                .IsEncryptedText(_encryptor, "Certificate.CredentialId");

            certificate.Property(entry => entry.CredentialUrl)
                .IsEncryptedUrl(_encryptor, "Certificate.CredentialUrl");

            certificate.Property(entry => entry.ValidityPeriod);
        });

        UseBackingField(builder, resume => resume.Certificates);
    }

    // MIXED, and the two halves must not be collapsed into one. Name and Level are PLAINTEXT scoring
    // inputs; Fluency is CONFIDENTIAL free text.
    //
    // Name and Level are what ScoringRules.EvaluateLanguage compares against the posting's
    // MinimumLevel, and it could not do that from behind an envelope. Their value sets are small,
    // closed and public, and Name carries the index every scoring join walks.
    //
    // FLUENCY IS ENCRYPTED, by ruling, and the ruling closed an inconsistency rather than adding
    // caution. Three reasons, in the order that makes it correct:
    //
    //   1. It is free text A PERSON WROTE ABOUT THEMSELVES. A candidate can type "nativo, aprendido de
    //      mi abuela colombiana", which describes the person, not a level. Nothing constrains it.
    //   2. It STOPPED BEING A SCORING INPUT in PR #16. Level is what the engine reads, and the engine
    //      is FORBIDDEN to read Fluency -- stated on Domain.Resumes.Language, on
    //      ScoringRules.EvaluateLanguage, and beside the Level mapping below -- because parsing prose
    //      into a level would read an unrecognised word as "not proficient" and score a native speaker
    //      zero. So sealing it costs no query. That is what makes this cheap now and would not have
    //      been before; it is display-only, and encrypting it ends no feature.
    //   3. Its STRUCTURAL TWINS one section over -- Education.Degree and Education.Grade, same shape,
    //      same display-only role -- were already encrypted. Fluency sitting in plaintext beside them
    //      was the finding.
    //
    // The rule in this file's header is unchanged and still decides it: "a skill, a level or a span of
    // time" is readable, and Level is exactly that. Fluency is not a level, it is prose ABOUT one.
    private void ConfigureLanguages(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.Languages, language =>
        {
            ChildTableOf(language, "Languages");

            language.Property(entry => entry.Name).HasMaxLength(100).IsRequired();

            // varbinary(max), not a pinned width, and the nvarchar(50) this replaces is genuinely
            // gone. That cap was persistence-only -- nothing in the Domain bounds Fluency -- and
            // EncryptedColumn pins a width only where the Domain bounds the plaintext, because a
            // guessed cap truncates an AES-GCM envelope, and a truncated envelope destroys the row
            // rather than the tail of a string: the tag lives in the last 16 bytes.
            language.Property(entry => entry.Fluency)
                .IsEncryptedText(_encryptor, "Language.Fluency");

            // Level is the column the engine reads; Fluency stays beside it as free text for display
            // and is never parsed into it. See the comment on Language.Level for why that direction
            // matters -- an unrecognized word would read as "not proficient" and score a native
            // speaker zero. Sealing the LEVEL would leave the engine with only the prose it must
            // never parse, and the section would silently score zero for everyone.
            language.Property(entry => entry.Level).HasColumnType("tinyint");

            language.HasIndex(nameof(Language.Name));
        });

        UseBackingField(builder, resume => resume.Languages);
    }

    private void ConfigureAwards(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.Awards, award =>
        {
            ChildTableOf(award, "Awards");

            // CONFIDENTIAL: an award title and its awarder are a public record naming the recipient.
            award.Property(entry => entry.Title)
                .IsRequired()
                .IsEncryptedText(_encryptor, "Award.Title");

            award.Property(entry => entry.Awarder)
                .IsEncryptedOrganizationName(_encryptor, "Award.Awarder");

            award.Property(entry => entry.Summary)
                .IsEncryptedText(_encryptor, "Award.Summary");

            award.Property(entry => entry.Date);
        });

        UseBackingField(builder, resume => resume.Awards);
    }

    private void ConfigurePublications(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.Publications, publication =>
        {
            ChildTableOf(publication, "Publications");

            // CONFIDENTIAL: a publication title plus a URL is a byline, which is a name.
            publication.Property(entry => entry.Title)
                .IsRequired()
                .IsEncryptedText(_encryptor, "Publication.Title");

            publication.Property(entry => entry.Publisher)
                .IsEncryptedOrganizationName(_encryptor, "Publication.Publisher");

            publication.Property(entry => entry.Url)
                .IsEncryptedUrl(_encryptor, "Publication.Url");

            publication.Property(entry => entry.Summary)
                .IsEncryptedText(_encryptor, "Publication.Summary");

            publication.Property(entry => entry.ReleaseDate);
        });

        UseBackingField(builder, resume => resume.Publications);
    }

    private void ConfigureInterests(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.Interests, interest =>
        {
            ChildTableOf(interest, "Interests");

            // CONFIDENTIAL, and the least obvious call on this page. Interests are not job data:
            // they routinely reveal religion, politics, health and sexuality, which is exactly the
            // special-category material that must never sit in a queryable column.
            interest.Property(entry => entry.Name)
                .IsRequired()
                .IsEncryptedText(_encryptor, "Interest.Name");

            interest.Property(entry => entry.Keywords)
                .IsRequired()
                .IsEncryptedStringList(_encryptor, "Interest.Keywords");
        });

        UseBackingField(builder, resume => resume.Interests);
    }

    private void ConfigureReferences(EntityTypeBuilder<Resume> builder)
    {
        builder.OwnsMany(resume => resume.References, reference =>
        {
            ChildTableOf(reference, "References");

            // CONFIDENTIAL, every column, with a twist worth naming: this is personal data about a
            // THIRD PARTY who never signed up. They cannot consent here and cannot delete it, so the
            // whole row is sealed without exception.
            reference.Property(entry => entry.Name)
                .IsRequired()
                .IsEncryptedText(_encryptor, "Reference.Name");

            reference.Property(entry => entry.Position)
                .IsEncryptedText(_encryptor, "Reference.Position");

            reference.Property(entry => entry.Company)
                .IsEncryptedOrganizationName(_encryptor, "Reference.Company");

            reference.Property(entry => entry.Email)
                .IsEncryptedEmail(_encryptor, "Reference.Email");

            reference.Property(entry => entry.PhoneNumber)
                .IsEncryptedPhoneNumber(_encryptor, "Reference.PhoneNumber");

            reference.Property(entry => entry.ReferenceText)
                .IsEncryptedText(_encryptor, "Reference.ReferenceText");
        });

        UseBackingField(builder, resume => resume.References);
    }

    private static void ChildTableOf<TEntry>(OwnedNavigationBuilder<Resume, TEntry> child, string tableName)
        where TEntry : class
    {
        child.ToTable(tableName, "resumes");
        child.WithOwner().HasForeignKey(ChildTable.ResumeForeignKey);
        child.Property<int>(ChildTable.Key).ValueGeneratedOnAdd();
        child.HasKey(ChildTable.Key);
        child.HasIndex(ChildTable.ResumeForeignKey);
    }

    // The one thing that must not be forgotten on any of the ten collections. Each getter returns
    // _entries.AsReadOnly(), so reading through the property hands EF a ReadOnlyCollection wrapper
    // it cannot add to; the change tracker needs the List behind it.
    private static void UseBackingField<TEntry>(
        EntityTypeBuilder<Resume> builder,
        System.Linq.Expressions.Expression<Func<Resume, IEnumerable<TEntry>?>> navigation)
        where TEntry : class =>
        builder.Navigation(navigation).UsePropertyAccessMode(PropertyAccessMode.Field);
}
