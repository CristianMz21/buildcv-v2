using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Persistence.Converters;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BuildCv.Infrastructure.Tests.Persistence;

// Asserts the SHAPE of the model. No database is involved — building a model opens no connection —
// so these run in the ordinary test pass and fail a pull request rather than a deployment.
//
// The point of most of them is the product's data classification: confidential data encrypted,
// analytical data plaintext and queryable. That split is only real if something checks it, because
// getting a column wrong in either direction is invisible at the call site.
public sealed class ModelConfigurationTests
{
    // The block that names the human. Held by BOTH aggregates that describe a candidate, for the same
    // reason and with the same verdict: full name, address, phone, personal site, the summary they wrote
    // about themselves, and their social handles. There is no analytical use for any of it.
    private static readonly string[] EncryptedContactColumns =
    [
        "ContactInformation.FullName",
        "ContactInformation.Email",
        "ContactInformation.PhoneNumber",
        "ContactInformation.Location",
        "ContactInformation.Website",
        "ContactInformation.Summary",
        "ContactInformation.Profiles",
    ];

    // The ten item collections a CV is made of, stated ONCE and expanded per owner below.
    //
    // Two aggregates hold them — a Resume and a CandidateProfile — and ONE mapping class configures
    // both (CvItemCollections), so this ledger mirrors that shape rather than listing thirty-three
    // entries twice. A second copy here would recreate exactly the drift that class exists to prevent:
    // the copy a reviewer did not update is the copy that keeps the plaintext.
    //
    // WHAT THE EXPANSION DOES NOT WEAKEN, because deriving anything in a test is how a test stops
    // failing. It derives the two owners' PATHS and nothing else: each owner names its own AAD prefix
    // at the expansion site below, so a prefix that is wrong in production still fails here. And the
    // property those prefixes exist for — that no envelope can be lifted from one of the two tables
    // into the other and still decrypt — is asserted independently and more strongly by
    // EncryptionContexts_MatchTheirConverterAndTheirColumn, which requires EVERY context in the model
    // to be unique.
    private static readonly string[] EncryptedCvItemColumns =
    [
        // Where a person worked, and what they wrote about it. Period and Type stay plaintext.
        "Experience.Organization", "Experience.Position", "Experience.Summary", "Experience.Highlights",

        // Where a person studied, and how well.
        "Education.Institution", "Education.Degree", "Education.FieldOfStudy", "Education.Grade",

        // Names and URLs that resolve to a named account. Technologies stay plaintext.
        "Project.Name", "Project.Description", "Project.RepositoryUrl", "Project.LiveDemoUrl",
        "Project.Highlights",

        // A credential id resolves to a named person on the issuer's site.
        "Certificate.Name", "Certificate.Issuer", "Certificate.CredentialId", "Certificate.CredentialUrl",

        // Free text a candidate wrote about THEMSELVES -- "nativo, aprendido de mi abuela colombiana"
        // describes the person, not a level. Sealing it costs no query: PR #16 made Language.Level the
        // scoring input and forbade the engine from reading this, so it is display-only. Its
        // structural twins Education.Degree and Education.Grade were already here; it was not.
        "Language.Fluency",

        "Award.Title", "Award.Awarder", "Award.Summary",

        "Publication.Title", "Publication.Publisher", "Publication.Url", "Publication.Summary",

        // Special-category material in practice: interests routinely reveal religion, politics,
        // health and sexuality.
        "Interest.Name", "Interest.Keywords",

        // Personal data about a THIRD PARTY who never signed up and cannot delete it.
        "Reference.Name", "Reference.Position", "Reference.Company", "Reference.Email",
        "Reference.PhoneNumber", "Reference.ReferenceText",
    ];

    // Everything that belongs to one aggregate and to no other, written out. Property path -> the AAD
    // context it is sealed under.
    private static readonly Dictionary<string, string> EncryptedColumnsOfOneOwner = new(StringComparer.Ordinal)
    {
        // The login identifier. Encrypted, and searchable only through its blind index.
        ["Account.Email"] = "Account.Email",

        // Reads like an opaque id and is one -- but it is a STABLE identifier for a person at a third
        // party, so in the clear it links a BuildCv account to a Google account for anybody who reaches
        // the table. Nothing queries it: sign-in resolves the account by the email blind index and
        // compares this in memory afterwards, so sealing it cost no query.
        ["Account.ExternalSubject"] = "Account.ExternalSubject",

        // A bearer credential: whoever holds the plaintext IS the user until it expires.
        ["RefreshToken.Token"] = "RefreshToken.Token",

        // What the candidate calls this CV among their others -- "CV para la entrevista en Globant"
        // names an employer they may have told nobody. It passes the test this repository actually
        // applies rather than the one it looks like: nothing queries it. No engine reads it, no index
        // needs it, no analytics groups by it, so sealing it costs no query.
        //
        // A profile has no counterpart on purpose: it is the candidate's whole history rather than one
        // document, so there is nothing to tell apart from its siblings.
        ["Resume.Name"] = "Resume.Name",

        // Generated advice, but not generic advice: the sentence quotes the resume and the posting it
        // was scored against back at the candidate. Its STRUCTURE — Section, Priority, Kind, Impact —
        // stays plaintext beside it, which is what keeps "which advice do we give most often"
        // answerable without the text.
        ["Analysis.Recommendation.Message"] = "Recommendation.Message",

        // The same judgement on the readability side, and if anything a stronger case. This sentence
        // quotes the candidate's own bullet points and job titles back at them ("Add an entry covering
        // the 14-month gap before 'Backend Developer'"), so a dump of readability.Recommendations would
        // read as a summary of every gap in every candidate's history.
        //
        // ITS OWN CONTEXT STRING, and that is the whole reason this entry is not "Recommendation.Message"
        // a second time. The context is the AAD: two columns sharing one would let an envelope be moved
        // between scoring.Recommendations and readability.Recommendations and still decrypt, which is
        // exactly the binding the context exists to create.
        // EncryptionContexts_MatchTheirConverterAndTheirColumn asserts that uniqueness.
        ["ReadabilityReport.ReadabilityRecommendation.Message"] = "ReadabilityRecommendation.Message",
    };

    // The classification, as a table a reviewer can read against the brief without deriving it from
    // seven configuration files.
    private static readonly Dictionary<string, string> ExpectedEncryptedColumns = Classification();

    private static Dictionary<string, string> Classification()
    {
        var columns = new Dictionary<string, string>(EncryptedColumnsOfOneOwner, StringComparer.Ordinal);

        // THE RESUME'S ITEM CONTEXTS ARE UNQUALIFIED, and only its may be. They were written that way
        // before a second owner existed and are the AAD of every envelope already on disk, so
        // prefixing them now would make every stored resume fail to decrypt. Its CONTACT block is the
        // exception that proves it was never a rule: those contexts were written "Resume.*" from the
        // start, so the prefix that reads like the owner-prefix mechanism predates it.
        AddOwned(columns, owner: "Resume", contextPrefix: "Resume.", EncryptedContactColumns);
        AddOwned(columns, owner: "Resume", contextPrefix: "", EncryptedCvItemColumns);

        // Every later owner carries one, which is what keeps its columns out of the resume's envelope
        // namespace. Copying an entry into a generated CV is unaffected: that copy happens in memory,
        // so the converter decrypts under this context and re-encrypts under the resume's.
        AddOwned(columns, owner: "CandidateProfile", contextPrefix: "CandidateProfile.", EncryptedContactColumns);
        AddOwned(columns, owner: "CandidateProfile", contextPrefix: "CandidateProfile.", EncryptedCvItemColumns);

        return columns;
    }

    private static void AddOwned(
        Dictionary<string, string> columns, string owner, string contextPrefix, string[] paths)
    {
        foreach (var path in paths)
            columns[$"{owner}.{path}"] = contextPrefix + path;
    }

    // The other half of the same requirement. Encrypting any of these would silently end a feature:
    // the scoring engine reads them, and internal analytics groups by them. Every entry here is a
    // column something really queries — Language.Fluency used to be the exception and has been moved
    // to ExpectedEncryptedColumns above, which is where it belongs now that it is sealed.
    //
    // A deliberate SPOT-CHECK, not an exhaustive set — it names the highest-value analytical columns
    // and omits the ones whose loss would be obvious or harmless (audit timestamps, the four
    // ScoreBreakdown scores not named below, Membership, the opaque Guid foreign keys). Completeness
    // is not needed here because ExpectedEncryptedColumns is asserted with exact set equality in BOTH
    // directions: nothing can gain encryption without being declared there, so nothing can slip out of
    // this list unnoticed. This one exists to make the intent of the classification legible.
    //
    // The CV-item half is expanded for BOTH owners, for the reason EncryptedCvItemColumns gives: one
    // mapping class configures them, so a list that named only the resume's would let the profile's
    // copy of a column be sealed without this test noticing that a query had been ended.
    private static readonly string[] AnalyticalCvItemColumns =
    [
        "Skill.Name", "Skill.Level", "Skill.YearsOfExperience", "Skill.Keywords",
        "Language.Name",
        "Project.Technologies", "Project.Period",
        "Experience.Type", "Experience.Period",
        "Education.Period",

        // The two dimensions the scorer could not previously see, on the candidate's side. Both are
        // levels, which is the plaintext half of the classification rule, and each sits beside a
        // free-text column saying roughly the same thing in prose. Both of those neighbours --
        // Education.Degree and Language.Fluency -- are now ENCRYPTED, and the pairing is the rule
        // rather than an accident: the LEVEL is the closed, comparable value the engine reads, and the
        // prose beside it is a sentence a person wrote about themselves.
        //
        // Sealing either LEVEL would leave the engine with only that prose -- which it must never
        // parse -- and the section would silently score zero for everyone. That is why these two are
        // on this list and their neighbours are not.
        "Language.Level", "Education.Level",
        "Certificate.ValidityPeriod",
        "Award.Date",
        "Publication.ReleaseDate",
    ];

    private static readonly string[] HighValueAnalyticalColumns =
    [
        .. AnalyticalCvItemColumns.Select(path => $"Resume.{path}"),
        .. AnalyticalCvItemColumns.Select(path => $"CandidateProfile.{path}"),

        "JobPosting.JobRequirement.Skill", "JobPosting.JobRequirement.Priority",
        "JobPosting.JobRequirement.Weight",
        "JobPosting.Title", "JobPosting.Description", "JobPosting.CompanyName", "JobPosting.Status",

        // The posting's side of the same two dimensions. JobPosting is wholly plaintext by design --
        // see the header comment on JobPostingConfiguration -- so these are here to state the intent,
        // not to defend a borderline call.
        "JobPosting.EducationLevel",
        "JobPosting.LanguageRequirement.Name", "JobPosting.LanguageRequirement.MinimumLevel",
        "Organization.Name", "Organization.Slug", "Organization.Status",
        "Account.Role", "Account.Status", "Account.FailedLoginCount",
        "Analysis.ScoredAt",
        "Analysis.ScoreBreakdown.SkillsScore", "Analysis.ScoreBreakdown.LanguagesScore",
        "Analysis.ScoreBreakdown.Weights",

        // The half of a recommendation that survives its message being sealed. Encrypting any of
        // these would not lose a column, it would lose the rollup the encryption was traded for.
        "Analysis.Recommendation.Section", "Analysis.Recommendation.Priority",
        "Analysis.Recommendation.Kind", "Analysis.Recommendation.Impact",

        // The readability side of the identical trade, and it belongs on THIS list rather than in a
        // test of its own for the reason stated above it: these four carry the (Section, Priority)
        // index that readability.Recommendations is grouped by, so they really are queried.
        "ReadabilityReport.ReadabilityRecommendation.Section",
        "ReadabilityReport.ReadabilityRecommendation.Priority",
        "ReadabilityReport.ReadabilityRecommendation.Kind",
        "ReadabilityReport.ReadabilityRecommendation.Impact",
    ];

    // ---------------------------------------------------------------------------------------------
    // THE BOUNDED PLAINTEXT LEDGER.
    //
    // Two EF Core events fire from INSIDE SaveChangesAsync — RelationalEventId.CommandError and
    // CoreEventId.SaveChangesFailed, both Error level with the exception attached — so by the time
    // SaveChangesExtensions catches SQL Server error 2628 and deliberately drops the inner exception,
    // EF has already written the message. 2628 is "String or binary data would be truncated", and
    // SQL Server composes that text with the OFFENDING VALUE quoted into it. Dropping the inner
    // exception guards a door the value has already walked through.
    //
    // Filtering the log is not the fix and was measured before being rejected: CommandError fires for
    // every failed DbCommand including reads, so silencing that category also hides connection
    // failures, timeouts, deadlocks and permission errors with no substitute signal — and it would not
    // close the hole anyway, because DbContext.SaveChangesFailed is a public .NET event and SQL
    // auto-instrumentation reads the ADO.NET layer directly. EnableSensitiveDataLogging is not in the
    // causal path either: it gates PARAMETER interpolation, never Exception.Message.
    //
    // So the defence has to be that the value never reaches SQL Server, which means every bounded
    // plaintext column that can be handed arbitrary text needs a Domain rule refusing it first —
    // Language.Name <= 100 is the precedent, added after exactly this error carried candidate text
    // into a log. The pair of sets below is that requirement written down, and the exact-set assertion
    // over them is what makes an OMISSION mechanical rather than something a reviewer has to remember:
    // the same job EncryptedColumns_AreExactlyTheClassifiedSet does for the classification above.
    //
    // TWO CATEGORIES, AND THE ISSUE THAT ROUTED THIS OVERSTATES THE FIRST. Its wording is "every
    // bounded plaintext column currently has a Domain rule". Twenty-four columns carry a width;
    // FOURTEEN have one. The other ten are numbers and dates whose width comes from the shape of their
    // serialization, and writing the issue's sentence at the top of a ledger that disproves it is the
    // defect this repository has spent its history removing — so it is corrected here rather than
    // repeated.
    //
    // THE PAIRING CANNOT BE INFERRED, which is why it is authored rather than reflected over. Nothing
    // joins a column to the rule that guards it: Skill.Name and JobRequirement.Skill are both defended
    // by Technology's limit, on a third type entirely; JobPosting.Description is a plain string whose
    // rule is imperative code inside a private ValidateDescription with no inspectable metadata; and
    // the constants are spelled five different ways — MaxLength, MaxNameLength, MaxTitleLength,
    // MaxDescriptionLength, MaxHashLength — with only LanguageRequirement.MaxNameLength public at all.
    // ---------------------------------------------------------------------------------------------

    // Column -> build a value of EXACTLY this many characters and push it through the Domain factory a
    // caller really goes through. Entity factories, not just the value object underneath, so a factory
    // that took a raw string past its value object would be caught here too.
    //
    // Each delegate is called TWICE, at the column's own width and at one character over, and both
    // halves carry weight:
    //
    //   - ONE OVER MUST THROW, or the rule is missing and the next over-long value is a 2628 in a log.
    //   - AT THE WIDTH MUST NOT THROW, and that is what makes the throw attributable to LENGTH. The two
    //     calls differ by one character and nothing else, so a factory that would have refused this
    //     shape of value at any size cannot pass by rejecting both.
    //
    // Together they also pin the two widths to each other: widen HasMaxLength without moving the Domain
    // rule and the at-the-width call starts throwing; narrow the Domain rule and it throws as well.
    //
    // The four fillers below are declared BEFORE the ledger on purpose: a static field read from an
    // earlier field's initializer is maybe-null to the compiler, whatever the runtime order turns out
    // to be.
    private static readonly Email SomeEmail = Email.Create("ledger@example.com");
    private static readonly AccountId SomeAccount = AccountId.New();
    private static readonly OrganizationName SomeCompany = OrganizationName.Create("Contoso");
    private static readonly Slug SomeSlug = Slug.Create("contoso");

    private static readonly Dictionary<string, Action<int>> BoundedByADomainLengthRule = new(StringComparer.Ordinal)
    {
        // The Argon2id hash, not the password. Through Account.Create so the entity factory is on the
        // path, since it is the only thing standing between IPasswordHasher's output and the column.
        ["Account.Password"] = length =>
            Account.Create(SomeEmail, Password.Create(Argon2idHashOf(length)), Role.Candidate),

        // A closed set today -- the value is a verifier's own Provider, never anything a caller sends.
        // It is in this ledger anyway, because "no free text reaches it" is a property of today's
        // callers rather than of the column, and this ledger exists to make that distinction stop
        // mattering.
        ["Account.ExternalProvider"] = length =>
            Account.CreateExternal(SomeEmail).LinkExternal(Filler(length), "a-subject"),

        ["JobPosting.CompanyName"] = length =>
            JobPosting.Create(SomeAccount, "Backend Developer", OrganizationName.Create(Filler(length))),
        ["JobPosting.Description"] = length =>
            JobPosting.Create(SomeAccount, "Backend Developer", SomeCompany, Filler(length)),
        ["JobPosting.Title"] = length =>
            JobPosting.Create(SomeAccount, Filler(length), SomeCompany),

        // Both sides of the Technology limit, because one column reaching it does not prove the other
        // does: these are different entities on different aggregates that happen to share a type.
        ["JobPosting.JobRequirement.Skill"] = length =>
            JobRequirement.Create(Technology.Create(Filler(length)), RequirementPriority.MustHave),

        // The same Domain factory guards the resume's column and the profile's, because one mapping
        // class configures both. Two entries anyway: the entry is what makes the COLUMN accounted for,
        // and an unlisted bounded column is the omission this ledger exists to catch.
        ["Resume.Skill.Name"] = length => Skill.Create(Technology.Create(Filler(length))),
        ["CandidateProfile.Skill.Name"] = length => Skill.Create(Technology.Create(Filler(length))),

        // The column the rule was written for, and its opposite number in the Jobs context. They do not
        // share a type on purpose — see the header on LanguageRequirement — so they need two entries.
        ["Resume.Language.Name"] = length => Language.Create(Filler(length)),
        ["CandidateProfile.Language.Name"] = length => Language.Create(Filler(length)),
        ["JobPosting.LanguageRequirement.Name"] = length =>
            LanguageRequirement.Create(Filler(length), LanguageProficiency.Professional),

        ["Organization.Name"] = length =>
            Organization.Create(OrganizationName.Create(Filler(length)), SomeSlug, SomeAccount),
        ["Organization.Slug"] = length =>
            Organization.Create(SomeCompany, Slug.Create(Filler(length)), SomeAccount),

        ["JobPosting.Responsibility.Description"] = length => Responsibility.Create(Filler(length)),
    };

    // The other ten. They carry a width and have NO Domain length rule, and adding one would be
    // inventing a guard against an input they cannot receive: nothing here is written from a string.
    //
    //   - The eight DateRange columns are serialized by DateRangeConverter as "<start>/<end>", each
    //     endpoint being PartialDate.ToIsoString — "yyyy-MM-dd", "yyyy-MM" or "yyyy", the year
    //     formatted D4 against DateOnly's own four-digit range. Twenty-one characters is the widest
    //     string that grammar can produce, so the column is exactly as wide as its encoding.
    //   - The two Weights columns hold one JSON object of a fixed arity of doubles plus an int
    //     SchemaVersion. No member of either payload is a string.
    //
    // What that buys is precisely the thing the ledger defends. Even if one of these DID overflow, the
    // value 2628 would quote back is a date range or an object of numbers — analytical data that is
    // plaintext on this row already — and not a sentence a candidate wrote. The categories differ in
    // what the leak would COST, which is why they are not merged and given one weaker rule.
    private static readonly string[] BoundedByTheirSerializedShape =
    [
        "Resume.Certificate.ValidityPeriod",
        "Resume.Education.Period",
        "Resume.Experience.Period",
        "Resume.Project.Period",
        "CandidateProfile.Certificate.ValidityPeriod",
        "CandidateProfile.Education.Period",
        "CandidateProfile.Experience.Period",
        "CandidateProfile.Project.Period",
        "ReadabilityReport.ReadabilityBreakdown.Weights",
        "Analysis.ScoreBreakdown.Weights",
    ];

    // 'a' survives every normalization these factories apply — Trim, Normalize(FormC), the
    // control-character refusals — and matches Slug's ^[a-z0-9]+(?:-[a-z0-9]+)*$, so the only thing
    // that ever varies between the two calls is the count.
    private static string Filler(int length) => new('a', length);

    // Password.Create reads the algorithm from between the first two '$', so the prefix has to be real
    // or the factory would refuse every length for the wrong reason -- which the at-the-width call
    // would catch, but only after wasting a reader's afternoon.
    private static string Argon2idHashOf(int length)
    {
        const string prefix = "$argon2id$";
        return prefix + Filler(length - prefix.Length);
    }

    // EIGHT. ReadabilityReport is an aggregate root of its own and not a part of Analysis: an Analysis
    // requires a non-nullable JobPostingId, and a readability report is taken with no posting in
    // existence. CandidateProfile is one for a different reason -- it outlives every CV made from it and
    // is owned by the ACCOUNT, which is the whole point of separating it from Resume. Both therefore
    // carry the same table shape every other root does -- audit columns, soft delete, a rowversion and
    // the clustered Seq index -- and this list is what makes "it is a root" a checked claim rather than
    // a sentence in a comment.
    private static readonly Type[] ExpectedAggregateRoots =
    [
        typeof(Account), typeof(RefreshToken), typeof(Resume),
        typeof(JobPosting), typeof(Organization), typeof(Analysis),
        typeof(ReadabilityReport), typeof(CandidateProfile),
    ];

    [Fact]
    public void Model_Builds_WithRealEncryptor()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var act = () => context.Model.GetEntityTypes().ToList();

        act.Should().NotThrow();
    }

    // The routed finding, stated as an assertion.
    //
    // EF will happily translate `Where(a => a.Email == someEmail)`: it applies ConvertToProvider to
    // the operand, which produces a FRESH random-nonce envelope as the SQL parameter. The query
    // matches nothing and throws nothing. A unique index on such a column is worse than useless — it
    // enforces nothing while looking like it does. So no encrypted column may appear in any key,
    // index or foreign key anywhere in the model.
    [Fact]
    public void EncryptedProperties_ParticipateInNoKeyOrIndex()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var offenders = new List<string>();
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            foreach (var key in entityType.GetKeys())
                offenders.AddRange(EncryptedIn(key.Properties, $"key on {Name(entityType)}"));

            foreach (var index in entityType.GetIndexes())
                offenders.AddRange(EncryptedIn(index.Properties, $"index on {Name(entityType)}"));

            foreach (var foreignKey in entityType.GetForeignKeys())
                offenders.AddRange(EncryptedIn(foreignKey.Properties, $"foreign key on {Name(entityType)}"));
        }

        offenders.Should().BeEmpty(
            "an encrypted column re-encrypts with a new nonce on every conversion, so an index over it "
            + "silently matches nothing instead of failing");
    }

    // The classification itself. Exact set equality in both directions: a new encrypted column has to
    // be declared here, and a column that quietly LOSES its encryption fails just as loudly.
    [Fact]
    public void EncryptedColumns_AreExactlyTheClassifiedSet()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var actual = EncryptedProperties(context.Model)
            .ToDictionary(
                entry => $"{Name(entry.EntityType)}.{entry.Property.Name}",
                entry => (string)entry.Property.FindAnnotation(PersistenceAnnotations.EncryptionContext)!.Value!,
                StringComparer.Ordinal);

        actual.Should().BeEquivalentTo(ExpectedEncryptedColumns);
    }

    [Fact]
    public void AnalyticalColumns_AreNotEncrypted()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var encrypted = EncryptedProperties(context.Model)
            .Select(entry => $"{Name(entry.EntityType)}.{entry.Property.Name}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var path in HighValueAnalyticalColumns)
        {
            FindProperty(context.Model, path).Should().NotBeNull(
                "{0} is expected to be a mapped, queryable column", path);
            encrypted.Should().NotContain(path,
                "{0} is analytical data the scoring engine and internal reporting have to query", path);
        }
    }

    // The omission catch. Exact set equality against the model, in both directions, so a bounded
    // plaintext column cannot be ADDED without landing in one of the two sets — which is the whole
    // point, because the failure this guards against is a column arriving with no Domain rule and
    // nobody noticing until an over-long value puts candidate text in an error log.
    //
    // Derived entirely at model-build time and needs no database: non-encrypted properties whose
    // GetMaxLength() is non-null. Encrypted columns are excluded because their width is the ENVELOPE's
    // rather than the plaintext's — every constant in EncryptedColumn is the envelope overhead over a
    // plaintext its value object has ALREADY capped, and each one names that cap — so an over-long
    // value is refused by the Domain long before there is anything to seal.
    [Fact]
    public void BoundedPlaintextColumns_AreExactlyTheClassifiedSet()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var actual = BoundedPlaintextProperties(context.Model)
            .Select(entry => $"{Name(entry.EntityType)}.{entry.Property.Name}");

        actual.Should().BeEquivalentTo(
            BoundedByADomainLengthRule.Keys.Concat(BoundedByTheirSerializedShape),
            "a bounded plaintext column with no Domain rule reaches SQL Server, and error 2628 quotes "
            + "the value it refused into a message EF logs before ValueTooLongException can drop it");
    }

    // The ledger itself. The width comes from the MODEL rather than from a literal beside the delegate,
    // so the two cannot be edited into agreement — moving HasMaxLength moves what this test demands.
    [Theory]
    [MemberData(nameof(FreeTextColumnPaths))]
    public void EachFreeTextColumn_IsRefusedByItsDomainRuleAtExactlyTheColumnWidth(string path)
    {
        using var context = PersistenceTestContext.ModelOnly();

        var declared = FindProperty(context.Model, path)?.GetMaxLength();
        declared.Should().NotBeNull("{0} is listed as a bounded plaintext column", path);
        var width = declared!.Value;

        var construct = BoundedByADomainLengthRule[path];

        construct.Invoking(build => build(width)).Should().NotThrow(
            "a value that exactly fills {0} is legal, and this is what makes the refusal below "
            + "attributable to length rather than to the factory disliking the value's shape", path);

        construct.Invoking(build => build(width + 1)).Should().Throw<DomainException>(
            "one character over {0} would otherwise reach SQL Server as error 2628, whose message "
            + "quotes the offending value and is logged from inside SaveChangesAsync", path);
    }

    // The other half of the classification, and it is a one-way guard on purpose: it cannot prove a
    // column is unreachable by free text, but it does stop a plain string column being parked in the
    // serialization-bounded set to dodge the ledger. A converted non-string property can only be
    // written through its converter, so its width is that converter's grammar rather than a caller's.
    [Theory]
    [MemberData(nameof(SerializationBoundedColumnPaths))]
    public void EachSerializationBoundedColumn_IsWrittenThroughAConverterAndNotFromAString(string path)
    {
        using var context = PersistenceTestContext.ModelOnly();

        var property = FindProperty(context.Model, path);

        property.Should().NotBeNull("{0} is listed as a bounded plaintext column", path);
        property!.ClrType.Should().NotBe(typeof(string),
            "{0} claims its width comes from its serialization, which a string column cannot claim", path);
        property.GetValueConverter().Should().NotBeNull(
            "{0} has no Domain length rule, so the converter is the only thing bounding it", path);
    }

    public static TheoryData<string> FreeTextColumnPaths() => [.. BoundedByADomainLengthRule.Keys];

    public static TheoryData<string> SerializationBoundedColumnPaths() => [.. BoundedByTheirSerializedShape];

    // The provenance columns, pinned separately rather than folded into HighValueAnalyticalColumns —
    // because that list's stated contract is "a column something really QUERIES", and these two are not
    // queried. They are loaded with the row and compared in memory, so the claim would be false and this
    // repo's signature defect is a comment asserting a property the code does not have.
    //
    // What has to hold for them is different, and all three parts fail silently:
    //
    //   - MAPPED. Ignore either one and provenance stops persisting: every analysis then reads back with
    //     null provenance, which is defined as stale, so the score is permanently "out of date" and the
    //     de-duplication in ScoreResumeHandler can never hit. No test of a score's VALUE would notice.
    //   - NULLABLE. A required column would make the additive migration impossible to apply to a table
    //     that already has rows, and would force a fabricated default — a lie about what those rows
    //     scored, in the one direction (looking fresh) that misleads a candidate.
    //   - PLAINTEXT. The other direction is already caught by EncryptedColumns_AreExactlyTheClassifiedSet,
    //     which is exact set equality; this asserts it locally so the requirement is legible here too.
    [Theory]
    [InlineData(nameof(Analysis.ResumeUpdatedAt))]
    [InlineData(nameof(Analysis.JobPostingUpdatedAt))]
    public void ProvenanceColumns_AreMappedNullableAndPlaintext(string propertyName)
    {
        using var context = PersistenceTestContext.ModelOnly();

        var property = context.Model.FindEntityType(typeof(Analysis))!.FindProperty(propertyName);

        property.Should().NotBeNull(
            "an unmapped {0} makes every analysis read as stale and never de-duplicate", propertyName);
        property!.IsNullable.Should().BeTrue(
            "a row written before this column existed cannot know what it scored");
        property.FindAnnotation(PersistenceAnnotations.Encrypted)?.Value.Should().NotBe(true,
            "these are timestamps about rows, not about a person");
    }

    // The import-signals columns, pinned here for the same reason the provenance ones above are and NOT
    // added to HighValueAnalyticalColumns: that list's stated contract is "a column something really
    // QUERIES", and these four are loaded with the resume and read in memory by the readability engine.
    // Writing them into that list would be this repository's signature defect — a comment asserting a
    // property the code does not have.
    //
    // Three things have to hold, and all three fail silently:
    //
    //   - MAPPED. Ignore any one and the value stops round-tripping: ATS-parseability would then be
    //     scored from a partly-null set of signals, or renormalized out of every report, and no test of
    //     a SCORE's value would notice because the section is only 0.10 of the total.
    //   - NULLABLE. A required column cannot be added to a table that already has rows without a
    //     fabricated default, and every default here is a claim about a document those resumes never had.
    //   - PLAINTEXT. Two closed enums, a bool and a page count — none of them can hold a fragment of the
    //     candidate's document, and all four are what the engine reads and what "how many candidates
    //     upload two-column PDFs" is answered from. The opposite direction is already covered by
    //     EncryptedColumns_AreExactlyTheClassifiedSet's exact set equality, which is deliberately left
    //     UNCHANGED by this feature: nothing here joined the encrypted set, and nothing left it.
    [Theory]
    [InlineData(nameof(ImportSignals.ColumnLayout))]
    [InlineData(nameof(ImportSignals.HadTextLayer))]
    [InlineData(nameof(ImportSignals.PageCount))]
    [InlineData(nameof(ImportSignals.Warnings))]
    public void ImportSignalColumns_AreMappedNullableAndPlaintext(string propertyName)
    {
        using var context = PersistenceTestContext.ModelOnly();

        var property = context.Model.FindEntityType(typeof(ImportSignals))!.FindProperty(propertyName);

        property.Should().NotBeNull(
            "an unmapped {0} silently drops the evidence the ATS-parseability section is scored from",
            propertyName);

        // IsColumnNullable, not IProperty.IsNullable. The CLR members of an owned value are non-nullable
        // by declaration -- ColumnLayout is an enum and HadTextLayer is a bool -- and EF reports that as
        // IsNullable == false while still emitting a nullable COLUMN, because the whole owned reference
        // is optional. The column is what the migration writes and what an existing row has to satisfy,
        // so the column is what this asserts.
        property!.IsColumnNullable(ResumesTable).Should().BeTrue(
            "a resume created before this column existed came from no document at all");
        property!.FindAnnotation(PersistenceAnnotations.Encrypted)?.Value.Should().NotBe(true,
            "these describe a PDF's shape, not a person");
    }

    private static readonly StoreObjectIdentifier ResumesTable =
        StoreObjectIdentifier.Table("Resumes", "resumes");

    // The owned reference itself, which the theory above cannot see: a REQUIRED navigation would make
    // every hand-built resume unloadable, and EF reports that as an exception at materialization rather
    // than at model build.
    [Fact]
    public void ImportSignals_AreAnOptionalOwnedReferenceOnTheResumeRow()
    {
        using var context = PersistenceTestContext.ModelOnly();
        var navigation = context.Model.FindEntityType(typeof(Resume))!
            .FindNavigation(nameof(Resume.ImportSignals));

        navigation.Should().NotBeNull();
        navigation!.ForeignKey.IsOwnership.Should().BeTrue("the signals belong to the resume, not beside it");

        // IsRequiredDependent is the one that answers "may this owned value be absent". ForeignKey
        // .IsRequired says the opposite thing for an owned reference -- it is about the PRINCIPAL end,
        // which is always required because the owner is the row -- and reads true for both of these.
        // ContactInformation is asserted beside it so the pair proves this really distinguishes them,
        // rather than being a property that happens to be false for everything.
        navigation.ForeignKey.IsRequiredDependent.Should().BeFalse(
            "a resume typed by hand has no document, and that is the ordinary case");
        context.Model.FindEntityType(typeof(Resume))!
            .FindNavigation(nameof(Resume.ContactInformation))!.ForeignKey.IsRequiredDependent
            .Should().BeTrue("ContactInformation is the required owned reference this one is contrasted with");

        navigation.TargetEntityType.GetTableName().Should().Be("Resumes",
            "four nullable columns on the row, not a join for a value that is read on every readability run");
    }

    // Catches the mistake the annotation exists for: a copy-pasted configuration block that keeps the
    // context string of the column it was copied from. The context is the AAD, so a wrong one does not
    // fail at write time — it fails on the first READ, in production, as an authentication-tag
    // mismatch on data that is now unrecoverable under the right context.
    [Fact]
    public void EncryptionContexts_MatchTheirConverterAndTheirColumn()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entityType, property) in EncryptedProperties(context.Model))
        {
            var path = $"{Name(entityType)}.{property.Name}";
            var annotated = (string)property.FindAnnotation(PersistenceAnnotations.EncryptionContext)!.Value!;

            property.GetValueConverter().Should().BeAssignableTo<IEncryptedConverter>(
                "{0} is annotated as encrypted", path);
            ((IEncryptedConverter)property.GetValueConverter()!).Context.Should().Be(annotated,
                "the annotation on {0} must not drift from the converter that actually seals it", path);

            annotated.Split('.')[^1].Should().Be(property.Name,
                "the last segment of the context for {0} must name the column it is bound to", path);

            seen.Should().NotContainKey(annotated,
                "two columns sharing an encryption context lets an envelope be moved between them undetected");
            seen[annotated] = path;
        }
    }

    // Second routed finding, model side: an encrypted column with an exact-match use case must have a
    // companion that can actually be looked up by, and that companion must point back at it.
    [Theory]
    [InlineData(typeof(Account), ShadowColumns.EmailHash, AccountEmailIndex.Context)]
    [InlineData(typeof(RefreshToken), ShadowColumns.TokenHash, RefreshTokenIndex.Context)]
    public void BlindIndexColumns_PairWithTheirEncryptedColumn(Type clrType, string hashColumn, string expectedContext)
    {
        using var context = PersistenceTestContext.ModelOnly();
        var entityType = context.Model.FindEntityType(clrType)!;

        var hash = entityType.FindProperty(hashColumn);
        hash.Should().NotBeNull();
        hash!.IsShadowProperty().Should().BeTrue("the Domain must not carry a persistence lookup token");
        hash.GetColumnType().Should().Be("binary(32)");
        hash.IsNullable.Should().BeFalse("a row that cannot be looked up is a row that cannot be logged into");
        hash.FindAnnotation(PersistenceAnnotations.BlindIndexFor)!.Value.Should().Be(expectedContext);

        entityType.GetIndexes()
            .Should().ContainSingle(index => index.IsUnique && index.Properties.Any(p => p.Name == hashColumn));

        // The companion has to name a context that really exists on this entity, or it indexes a
        // digest of something nothing ever writes.
        EncryptedProperties(context.Model)
            .Where(entry => entry.EntityType == entityType)
            .Select(entry => (string)entry.Property.FindAnnotation(PersistenceAnnotations.EncryptionContext)!.Value!)
            .Should().Contain(expectedContext);
    }

    [Fact]
    public void AggregateRoots_CarryTheSharedTableShape()
    {
        using var context = PersistenceTestContext.ModelOnly();
        var model = PersistenceTestContext.DesignTimeModel(context);

        foreach (var clrType in ExpectedAggregateRoots)
        {
            var entityType = model.FindEntityType(clrType)!;
            var name = Name(entityType);

            entityType.FindProperty(ShadowColumns.Seq).Should().NotBeNull("{0} needs a keyset cursor", name);
            entityType.FindProperty(ShadowColumns.RowVersion).Should().NotBeNull("{0} needs a concurrency token", name);
            entityType.FindProperty(ShadowColumns.CreatedBy).Should().NotBeNull("{0} needs audit columns", name);
            entityType.FindProperty(ShadowColumns.UpdatedBy).Should().NotBeNull("{0} needs audit columns", name);
            entityType.FindProperty(ShadowColumns.DeletedAt).Should().NotBeNull("{0} needs soft delete", name);
            entityType.FindProperty(ShadowColumns.DeletedBy).Should().NotBeNull("{0} needs soft delete", name);

            entityType.FindProperty(ShadowColumns.RowVersion)!.IsConcurrencyToken.Should().BeTrue();

            entityType.FindPrimaryKey()!.IsClustered().Should().BeFalse(
                "{0} clusters on Seq; a random Guid as the clustered key fragments every insert", name);
            entityType.GetIndexes()
                .Should().ContainSingle(index =>
                    index.IsUnique && index.IsClustered() == true && index.Properties[0].Name == ShadowColumns.Seq,
                    "{0} needs its clustered unique Seq index", name);

            entityType.GetDeclaredQueryFilters().Should().NotBeEmpty(
                "{0} soft-deletes, so tombstoned rows must be filtered out by default", name);
        }
    }

    // The ten resume collections, the ten identical ones on CandidateProfile, plus the six elsewhere:
    // three on JobPosting (Requirements, LanguageRequirements, Responsibilities), Organization.Members,
    // Analysis.Recommendations and ReadabilityReport.Recommendations. Every getter returns
    // _entries.AsReadOnly(), so EF reading through the property gets a ReadOnlyCollection it cannot add
    // to; the failure is an exception on the first child insert, not at model build.
    [Fact]
    public void OwnedCollections_UseTheBackingField()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var collections = context.Model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetNavigations())
            .Where(navigation => navigation.IsCollection)
            .ToList();

        collections.Should().HaveCount(26);
        collections.Should().OnlyContain(navigation =>
            navigation.GetPropertyAccessMode() == PropertyAccessMode.Field
            && navigation.FieldInfo != null);
    }

    [Theory]
    [InlineData(typeof(Account), nameof(Account.IsEmailVerified))]
    [InlineData(typeof(Account), nameof(Account.IsLocked))]
    [InlineData(typeof(Account), nameof(Account.CanPostJobs))]
    [InlineData(typeof(RefreshToken), nameof(RefreshToken.IsExpired))]
    [InlineData(typeof(Analysis), nameof(Analysis.OverallScore))]
    [InlineData(typeof(Analysis), nameof(Analysis.Band))]
    [InlineData(typeof(ScoreBreakdown), nameof(ScoreBreakdown.WeightedTotal))]
    // Sections is the one whose failure would not look like a stale column: left mapped, EF tries to
    // discover SectionScore as an ENTITY TYPE and the model build throws, so every test in this file
    // fails at once rather than this one assertion.
    [InlineData(typeof(ScoreBreakdown), nameof(ScoreBreakdown.Sections))]
    // The readability aggregate carries the same four computed members and the same rule applies to
    // each: a persisted ReadabilityScore or Band would silently keep the old classification the first
    // time the band thresholds move, and a mapped Sections would take the whole model build with it.
    [InlineData(typeof(ReadabilityReport), nameof(ReadabilityReport.ReadabilityScore))]
    [InlineData(typeof(ReadabilityReport), nameof(ReadabilityReport.Band))]
    [InlineData(typeof(ReadabilityBreakdown), nameof(ReadabilityBreakdown.WeightedTotal))]
    [InlineData(typeof(ReadabilityBreakdown), nameof(ReadabilityBreakdown.Sections))]
    public void ComputedMembers_AreNotMapped(Type clrType, string propertyName)
    {
        using var context = PersistenceTestContext.ModelOnly();

        context.Model.FindEntityType(clrType)!.FindProperty(propertyName).Should().BeNull(
            "a persisted copy of a computed member becomes a second source of truth that goes stale");
    }

    // Value objects must map as converted scalars. If a converter is ever dropped, EF does not fail —
    // it discovers the type as an owned entity with its own table, and DateRange.IsCurrent, Url.Uri or
    // Password.Algorithm appear as columns. Pinning the entity-type set catches that.
    [Fact]
    public void Model_ContainsOnlyTheExpectedEntityTypes()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var actual = context.Model.GetEntityTypes().Select(Name).Order(StringComparer.Ordinal);

        // SectionScore and ReadabilitySectionScore are deliberately absent: each is projected from its
        // breakdown's own columns and Ignored, so an appearance here would mean the projection had
        // become a table.
        // ImportSignals IS here, unlike SectionScore: it is a real optional owned reference mapped into
        // the Resumes row as four Import_* columns, so EF has to discover it as an entity type. Its
        // absence would mean the whole value had stopped persisting. It appears under Resume ONLY —
        // a profile is fed from several documents, so "the document this came from" has no single answer
        // there and the value is deliberately not owned by it.
        actual.Should().Equal(
            "Account",
            "Analysis", "Analysis.Recommendation", "Analysis.ScoreBreakdown",
            "CandidateProfile",
            "CandidateProfile.Award", "CandidateProfile.Certificate",
            "CandidateProfile.ContactInformation", "CandidateProfile.Education",
            "CandidateProfile.Experience", "CandidateProfile.Interest", "CandidateProfile.Language",
            "CandidateProfile.Project", "CandidateProfile.Publication", "CandidateProfile.Reference",
            "CandidateProfile.Skill",
            "JobPosting", "JobPosting.JobRequirement", "JobPosting.LanguageRequirement",
            "JobPosting.Responsibility",
            "Organization", "Organization.Membership",
            "ReadabilityReport", "ReadabilityReport.ReadabilityBreakdown",
            "ReadabilityReport.ReadabilityRecommendation",
            "RefreshToken",
            "Resume",
            "Resume.Award", "Resume.Certificate", "Resume.ContactInformation", "Resume.Education",
            "Resume.Experience", "Resume.ImportSignals", "Resume.Interest", "Resume.Language",
            "Resume.Project", "Resume.Publication", "Resume.Reference", "Resume.Skill");
    }

    private static IEnumerable<(IEntityType EntityType, IProperty Property)> EncryptedProperties(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetProperties().Select(property => (entityType, property)))
            .Where(entry => entry.property.FindAnnotation(PersistenceAnnotations.Encrypted)?.Value is true);

    private static IEnumerable<(IEntityType EntityType, IProperty Property)> BoundedPlaintextProperties(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetProperties().Select(property => (entityType, property)))
            .Where(entry => entry.property.FindAnnotation(PersistenceAnnotations.Encrypted)?.Value is not true)
            .Where(entry => entry.property.GetMaxLength() is not null);

    private static IEnumerable<string> EncryptedIn(IEnumerable<IProperty> properties, string where) =>
        properties
            .Where(property => property.FindAnnotation(PersistenceAnnotations.Encrypted)?.Value is true)
            .Select(property => $"{property.Name} in {where}");

    private static IProperty? FindProperty(IModel model, string path)
    {
        var separator = path.LastIndexOf('.');
        var owner = path[..separator];
        return model.GetEntityTypes()
            .FirstOrDefault(entityType => Name(entityType) == owner)
            ?.FindProperty(path[(separator + 1)..]);
    }

    // An owned type is named by its OWNER, and that stopped being cosmetic the day a second aggregate
    // started owning the same ten item types. "Experience.Organization" now describes TWO columns, in
    // two tables, under two different AAD contexts — so a ledger keyed on the bare CLR name would
    // silently collapse them into one entry, and the entry that survived would be whichever the model
    // happened to enumerate last. Half the classification would then be unasserted while every test
    // stayed green, which is the one failure mode this file exists to not have.
    private static string Name(IReadOnlyEntityType entityType) =>
        entityType.FindOwnership() is { } ownership
            ? $"{Name(ownership.PrincipalEntityType)}.{entityType.ClrType.Name}"
            : entityType.ClrType.Name;
}
