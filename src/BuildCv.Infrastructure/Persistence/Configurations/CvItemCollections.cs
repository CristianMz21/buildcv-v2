using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Configurations;

/// <summary>
/// The one statement of how a CV's ten item collections are stored, shared by every aggregate that owns
/// them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where the product's data classification lives or dies</b>, and it is a shared class
/// precisely because there are now two owners of these collections: a <see cref="Resume"/> and a
/// <see cref="Domain.Candidates.CandidateProfile"/>. Copying the mapping into the second one would
/// create a second place for the classification to live, and the first change to either — a new column,
/// a re-classification, a context string — would silently apply to one and not the other. The half that
/// kept the plaintext would be the half nobody noticed.
/// </para>
/// <para>
/// The rule applied throughout: a value that IDENTIFIES OR DESCRIBES A PERSON is encrypted; a value that
/// describes a SKILL, A LEVEL OR A SPAN OF TIME is plaintext. So "who you worked for" and "what you
/// wrote about yourself" go into envelopes, while "C#", "Advanced", "4 years" and
/// "2020-01-01/2023-06-30" stay queryable — those are the columns the scoring engine and the internal
/// analytics read, and no query can reach through an envelope.
/// </para>
/// <para>
/// The split is not cosmetic. Put <c>Skill.Name</c> behind encryption and skill-gap analytics becomes
/// impossible; leave <c>Experience.Organization</c> in plaintext and a database dump tells an attacker
/// where every candidate on the platform works.
/// </para>
/// <para>
/// <b>What is shared is the classification, NOT the AAD.</b> Each owner passes its own
/// <c>contextPrefix</c>, so <c>candidates.Experiences.Organization</c> seals under
/// "CandidateProfile.Experience.Organization" while the resume's column keeps "Experience.Organization".
/// That is the whole point of the AAD: it decides which column an envelope may decrypt in, and one
/// shared string across two tables would let a ciphertext be moved between them at the DATABASE level
/// and still open — the move <c>SchemaRoundTripTests</c> executes and asserts fails for the two
/// recommendation tables. Copying an entry from a profile into a generated CV is unaffected, because
/// that copy happens in memory: the converter decrypts under the source column's context and re-encrypts
/// under the destination's.
/// </para>
/// <para>
/// <b>The resume's prefix is empty, and that is history rather than taste.</b> Its contexts were written
/// unqualified and are on disk in every row already encrypted, so qualifying them now would make every
/// existing resume fail to decrypt. A prefix is exactly the thing that cannot be changed after the first
/// write; new owners get one, and the first one does not.
/// </para>
/// </remarks>
internal sealed class CvItemCollections(IFieldEncryptor encryptor, string contextPrefix)
{
    private readonly IFieldEncryptor _encryptor = encryptor;
    private readonly string _contextPrefix = contextPrefix;

    /// <summary>The AAD for one column: this owner's prefix, then the item path.</summary>
    private string Aad(string columnPath) => _contextPrefix + columnPath;

    public void Experiences<TOwner>(OwnedNavigationBuilder<TOwner, Experience> experience, string schema, string foreignKey)
        where TOwner : class
    {
        ChildTableOf(experience, "Experiences", schema, foreignKey);

        // PLAINTEXT: professional vs volunteer is a category, not an identity.
        experience.Property(entry => entry.Type).HasColumnType("tinyint").IsRequired();

        // CONFIDENTIAL: the employer and the role name together identify a person more reliably
        // than most direct identifiers do.
        experience.Property(entry => entry.Organization)
            .IsRequired()
            .IsEncryptedOrganizationName(_encryptor, Aad("Experience.Organization"));

        experience.Property(entry => entry.Position)
            .IsRequired()
            .IsEncryptedText(_encryptor, Aad("Experience.Position"));

        experience.Property(entry => entry.Summary)
            .IsEncryptedText(_encryptor, Aad("Experience.Summary"));

        experience.Property(entry => entry.Highlights)
            .IsRequired()
            .IsEncryptedStringList(_encryptor, Aad("Experience.Highlights"));

        // PLAINTEXT: tenure length is what "years of experience" scoring is computed from.
        experience.Property(entry => entry.Period).IsRequired();
    }

    public void Educations<TOwner>(OwnedNavigationBuilder<TOwner, Education> education, string schema, string foreignKey)
        where TOwner : class
    {
        ChildTableOf(education, "Educations", schema, foreignKey);

        // CONFIDENTIAL: school, degree and grade are re-identifying, and grade in particular is
        // the kind of thing a candidate would never expect to be readable in a dump.
        education.Property(entry => entry.Institution)
            .IsRequired()
            .IsEncryptedOrganizationName(_encryptor, Aad("Education.Institution"));

        education.Property(entry => entry.Degree)
            .IsEncryptedText(_encryptor, Aad("Education.Degree"));

        education.Property(entry => entry.FieldOfStudy)
            .IsEncryptedText(_encryptor, Aad("Education.FieldOfStudy"));

        education.Property(entry => entry.Grade)
            .IsEncryptedText(_encryptor, Aad("Education.Grade"));

        education.Property(entry => entry.Period).IsRequired();

        // PLAINTEXT, joining Period as the second column here that is. A rung on a ladder is not
        // a description of a person: it is what the engine compares against a posting's required
        // level, and it could not do that from behind the envelope Degree sits in.
        education.Property(entry => entry.Level).HasColumnType("tinyint");
    }

    // Wholly PLAINTEXT, and deliberately so. "Which skills at which level for how long" is the
    // corpus every match score is computed against and the only table internal analytics can group
    // by. A skill name is a fact about a technology, not about a person.
    //
    // STATIC, and that is the evidence rather than a style choice: a method that encrypts nothing needs
    // no encryptor and no AAD prefix, so the signature itself says this collection is readable.
    public static void Skills<TOwner>(OwnedNavigationBuilder<TOwner, Skill> skill, string schema, string foreignKey)
        where TOwner : class
    {
        ChildTableOf(skill, "Skills", schema, foreignKey);

        skill.Property(entry => entry.Name).IsRequired();
        skill.Property(entry => entry.Level).HasColumnType("tinyint");
        skill.Property(entry => entry.YearsOfExperience);

        skill.Property(entry => entry.Keywords)
            .HasConversion<StringListConverter>(ConvertedComparers.ForList<string>())
            .IsRequired();

        // The join every scoring run makes: find resumes whose skills match a posting's
        // requirements.
        skill.HasIndex(nameof(Skill.Name));
    }

    public void Projects<TOwner>(OwnedNavigationBuilder<TOwner, Project> project, string schema, string foreignKey)
        where TOwner : class
    {
        ChildTableOf(project, "Projects", schema, foreignKey);

        // CONFIDENTIAL: a project name plus its repository URL is a direct link to a named GitHub
        // account. The description and highlights are free text the candidate wrote.
        project.Property(entry => entry.Name)
            .IsRequired()
            .IsEncryptedText(_encryptor, Aad("Project.Name"));

        project.Property(entry => entry.Description)
            .IsEncryptedText(_encryptor, Aad("Project.Description"));

        project.Property(entry => entry.RepositoryUrl)
            .IsEncryptedUrl(_encryptor, Aad("Project.RepositoryUrl"));

        project.Property(entry => entry.LiveDemoUrl)
            .IsEncryptedUrl(_encryptor, Aad("Project.LiveDemoUrl"));

        project.Property(entry => entry.Highlights)
            .IsRequired()
            .IsEncryptedStringList(_encryptor, Aad("Project.Highlights"));

        // PLAINTEXT: the technology list is scoring input, exactly like Skills. It says what was
        // used, not who used it.
        project.Property(entry => entry.Technologies)
            .HasConversion<TechnologyListConverter>(ConvertedComparers.ForList<Technology>())
            .IsRequired();

        project.Property(entry => entry.Period).IsRequired();
    }

    public void Certificates<TOwner>(OwnedNavigationBuilder<TOwner, Certificate> certificate, string schema, string foreignKey)
        where TOwner : class
    {
        ChildTableOf(certificate, "Certificates", schema, foreignKey);

        // CONFIDENTIAL: a credential id and its verification URL resolve to a named person on
        // the issuer's site.
        certificate.Property(entry => entry.Name)
            .IsRequired()
            .IsEncryptedText(_encryptor, Aad("Certificate.Name"));

        certificate.Property(entry => entry.Issuer)
            .IsRequired()
            .IsEncryptedOrganizationName(_encryptor, Aad("Certificate.Issuer"));

        certificate.Property(entry => entry.CredentialId)
            .IsEncryptedText(_encryptor, Aad("Certificate.CredentialId"));

        certificate.Property(entry => entry.CredentialUrl)
            .IsEncryptedUrl(_encryptor, Aad("Certificate.CredentialUrl"));

        certificate.Property(entry => entry.ValidityPeriod);
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
    public void Languages<TOwner>(OwnedNavigationBuilder<TOwner, Language> language, string schema, string foreignKey)
        where TOwner : class
    {
        ChildTableOf(language, "Languages", schema, foreignKey);

        language.Property(entry => entry.Name).HasMaxLength(100).IsRequired();

        // varbinary(max), not a pinned width, and the nvarchar(50) this replaces is genuinely
        // gone. That cap was persistence-only -- nothing in the Domain bounds Fluency -- and
        // EncryptedColumn pins a width only where the Domain bounds the plaintext, because a
        // guessed cap truncates an AES-GCM envelope, and a truncated envelope destroys the row
        // rather than the tail of a string: the tag lives in the last 16 bytes.
        language.Property(entry => entry.Fluency)
            .IsEncryptedText(_encryptor, Aad("Language.Fluency"));

        // Level is the column the engine reads; Fluency stays beside it as free text for display
        // and is never parsed into it. See the comment on Language.Level for why that direction
        // matters -- an unrecognized word would read as "not proficient" and score a native
        // speaker zero. Sealing the LEVEL would leave the engine with only the prose it must
        // never parse, and the section would silently score zero for everyone.
        language.Property(entry => entry.Level).HasColumnType("tinyint");

        language.HasIndex(nameof(Language.Name));
    }

    public void Awards<TOwner>(OwnedNavigationBuilder<TOwner, Award> award, string schema, string foreignKey)
        where TOwner : class
    {
        ChildTableOf(award, "Awards", schema, foreignKey);

        // CONFIDENTIAL: an award title and its awarder are a public record naming the recipient.
        award.Property(entry => entry.Title)
            .IsRequired()
            .IsEncryptedText(_encryptor, Aad("Award.Title"));

        award.Property(entry => entry.Awarder)
            .IsEncryptedOrganizationName(_encryptor, Aad("Award.Awarder"));

        award.Property(entry => entry.Summary)
            .IsEncryptedText(_encryptor, Aad("Award.Summary"));

        award.Property(entry => entry.Date);
    }

    public void Publications<TOwner>(OwnedNavigationBuilder<TOwner, Publication> publication, string schema, string foreignKey)
        where TOwner : class
    {
        ChildTableOf(publication, "Publications", schema, foreignKey);

        // CONFIDENTIAL: a publication title plus a URL is a byline, which is a name.
        publication.Property(entry => entry.Title)
            .IsRequired()
            .IsEncryptedText(_encryptor, Aad("Publication.Title"));

        publication.Property(entry => entry.Publisher)
            .IsEncryptedOrganizationName(_encryptor, Aad("Publication.Publisher"));

        publication.Property(entry => entry.Url)
            .IsEncryptedUrl(_encryptor, Aad("Publication.Url"));

        publication.Property(entry => entry.Summary)
            .IsEncryptedText(_encryptor, Aad("Publication.Summary"));

        publication.Property(entry => entry.ReleaseDate);
    }

    public void Interests<TOwner>(OwnedNavigationBuilder<TOwner, Interest> interest, string schema, string foreignKey)
        where TOwner : class
    {
        ChildTableOf(interest, "Interests", schema, foreignKey);

        // CONFIDENTIAL, and the least obvious call on this page. Interests are not job data:
        // they routinely reveal religion, politics, health and sexuality, which is exactly the
        // special-category material that must never sit in a queryable column.
        interest.Property(entry => entry.Name)
            .IsRequired()
            .IsEncryptedText(_encryptor, Aad("Interest.Name"));

        interest.Property(entry => entry.Keywords)
            .IsRequired()
            .IsEncryptedStringList(_encryptor, Aad("Interest.Keywords"));
    }

    public void References<TOwner>(OwnedNavigationBuilder<TOwner, Reference> reference, string schema, string foreignKey)
        where TOwner : class
    {
        ChildTableOf(reference, "References", schema, foreignKey);

        // CONFIDENTIAL, every column, with a twist worth naming: this is personal data about a
        // THIRD PARTY who never signed up. They cannot consent here and cannot delete it, so the
        // whole row is sealed without exception.
        reference.Property(entry => entry.Name)
            .IsRequired()
            .IsEncryptedText(_encryptor, Aad("Reference.Name"));

        reference.Property(entry => entry.Position)
            .IsEncryptedText(_encryptor, Aad("Reference.Position"));

        reference.Property(entry => entry.Company)
            .IsEncryptedOrganizationName(_encryptor, Aad("Reference.Company"));

        reference.Property(entry => entry.Email)
            .IsEncryptedEmail(_encryptor, Aad("Reference.Email"));

        reference.Property(entry => entry.PhoneNumber)
            .IsEncryptedPhoneNumber(_encryptor, Aad("Reference.PhoneNumber"));

        reference.Property(entry => entry.ReferenceText)
            .IsEncryptedText(_encryptor, Aad("Reference.ReferenceText"));
    }

    /// <summary>The child-table shape every one of these collections shares.</summary>
    private static void ChildTableOf<TOwner, TEntry>(
        OwnedNavigationBuilder<TOwner, TEntry> child, string tableName, string schema, string foreignKey)
        where TOwner : class
        where TEntry : class
    {
        child.ToTable(tableName, schema);
        child.WithOwner().HasForeignKey(foreignKey);
        child.Property<int>(ChildTable.Key).ValueGeneratedOnAdd();
        child.HasKey(ChildTable.Key);
        child.HasIndex(foreignKey);
    }
}
