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
    // The classification, stated once, as a table a reviewer can read against the brief without
    // deriving it from six configuration files. Property path -> the AAD context it is sealed under.
    private static readonly Dictionary<string, string> ExpectedEncryptedColumns = new(StringComparer.Ordinal)
    {
        // The login identifier. Encrypted, and searchable only through its blind index.
        ["Account.Email"] = "Account.Email",

        // A bearer credential: whoever holds the plaintext IS the user until it expires.
        ["RefreshToken.Token"] = "RefreshToken.Token",

        // The block that names the human.
        ["ContactInformation.FullName"] = "Resume.ContactInformation.FullName",
        ["ContactInformation.Email"] = "Resume.ContactInformation.Email",
        ["ContactInformation.PhoneNumber"] = "Resume.ContactInformation.PhoneNumber",
        ["ContactInformation.Location"] = "Resume.ContactInformation.Location",
        ["ContactInformation.Website"] = "Resume.ContactInformation.Website",
        ["ContactInformation.Summary"] = "Resume.ContactInformation.Summary",
        ["ContactInformation.Profiles"] = "Resume.ContactInformation.Profiles",

        // Where a person worked, and what they wrote about it. Period and Type stay plaintext.
        ["Experience.Organization"] = "Experience.Organization",
        ["Experience.Position"] = "Experience.Position",
        ["Experience.Summary"] = "Experience.Summary",
        ["Experience.Highlights"] = "Experience.Highlights",

        // Where a person studied, and how well.
        ["Education.Institution"] = "Education.Institution",
        ["Education.Degree"] = "Education.Degree",
        ["Education.FieldOfStudy"] = "Education.FieldOfStudy",
        ["Education.Grade"] = "Education.Grade",

        // Names and URLs that resolve to a named account. Technologies stay plaintext.
        ["Project.Name"] = "Project.Name",
        ["Project.Description"] = "Project.Description",
        ["Project.RepositoryUrl"] = "Project.RepositoryUrl",
        ["Project.LiveDemoUrl"] = "Project.LiveDemoUrl",
        ["Project.Highlights"] = "Project.Highlights",

        // A credential id resolves to a named person on the issuer's site.
        ["Certificate.Name"] = "Certificate.Name",
        ["Certificate.Issuer"] = "Certificate.Issuer",
        ["Certificate.CredentialId"] = "Certificate.CredentialId",
        ["Certificate.CredentialUrl"] = "Certificate.CredentialUrl",

        // Free text a candidate wrote about THEMSELVES -- "nativo, aprendido de mi abuela colombiana"
        // describes the person, not a level. Sealing it costs no query: PR #16 made Language.Level the
        // scoring input and forbade the engine from reading this, so it is display-only. Its
        // structural twins Education.Degree and Education.Grade were already here; it was not.
        ["Language.Fluency"] = "Language.Fluency",

        ["Award.Title"] = "Award.Title",
        ["Award.Awarder"] = "Award.Awarder",
        ["Award.Summary"] = "Award.Summary",

        ["Publication.Title"] = "Publication.Title",
        ["Publication.Publisher"] = "Publication.Publisher",
        ["Publication.Url"] = "Publication.Url",
        ["Publication.Summary"] = "Publication.Summary",

        // Special-category material in practice: interests routinely reveal religion, politics,
        // health and sexuality.
        ["Interest.Name"] = "Interest.Name",
        ["Interest.Keywords"] = "Interest.Keywords",

        // Personal data about a THIRD PARTY who never signed up and cannot delete it.
        ["Reference.Name"] = "Reference.Name",
        ["Reference.Position"] = "Reference.Position",
        ["Reference.Company"] = "Reference.Company",
        ["Reference.Email"] = "Reference.Email",
        ["Reference.PhoneNumber"] = "Reference.PhoneNumber",
        ["Reference.ReferenceText"] = "Reference.ReferenceText",

        // Generated advice, but not generic advice: the sentence quotes the resume and the posting it
        // was scored against back at the candidate. Its STRUCTURE — Section, Priority, Kind, Impact —
        // stays plaintext beside it, which is what keeps "which advice do we give most often"
        // answerable without the text.
        ["Recommendation.Message"] = "Recommendation.Message",

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
        ["ReadabilityRecommendation.Message"] = "ReadabilityRecommendation.Message",
    };

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
    private static readonly string[] HighValueAnalyticalColumns =
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
        "JobRequirement.Skill", "JobRequirement.Priority", "JobRequirement.Weight",
        "JobPosting.Title", "JobPosting.Description", "JobPosting.CompanyName", "JobPosting.Status",

        // The posting's side of the same two dimensions. JobPosting is wholly plaintext by design --
        // see the header comment on JobPostingConfiguration -- so these are here to state the intent,
        // not to defend a borderline call.
        "JobPosting.EducationLevel", "LanguageRequirement.Name", "LanguageRequirement.MinimumLevel",
        "Organization.Name", "Organization.Slug", "Organization.Status",
        "Account.Role", "Account.Status", "Account.FailedLoginCount",
        "Analysis.ScoredAt",
        "ScoreBreakdown.SkillsScore", "ScoreBreakdown.LanguagesScore", "ScoreBreakdown.Weights",

        // The half of a recommendation that survives its message being sealed. Encrypting any of
        // these would not lose a column, it would lose the rollup the encryption was traded for.
        "Recommendation.Section", "Recommendation.Priority", "Recommendation.Kind", "Recommendation.Impact",

        // The readability side of the identical trade, and it belongs on THIS list rather than in a
        // test of its own for the reason stated above it: these four carry the (Section, Priority)
        // index that readability.Recommendations is grouped by, so they really are queried.
        "ReadabilityRecommendation.Section", "ReadabilityRecommendation.Priority",
        "ReadabilityRecommendation.Kind", "ReadabilityRecommendation.Impact",
    ];

    // SEVEN, not six. ReadabilityReport is an aggregate root of its own and not a part of Analysis:
    // an Analysis requires a non-nullable JobPostingId, and a readability report is taken with no
    // posting in existence. It therefore has to carry the same table shape every other root does --
    // audit columns, soft delete, a rowversion and the clustered Seq index -- and this list is what
    // makes "it is a root" a checked claim rather than a sentence in a comment.
    private static readonly Type[] ExpectedAggregateRoots =
    [
        typeof(Account), typeof(RefreshToken), typeof(Resume),
        typeof(JobPosting), typeof(Organization), typeof(Analysis),
        typeof(ReadabilityReport),
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

    // The ten resume collections plus the six elsewhere: three on JobPosting (Requirements,
    // LanguageRequirements, Responsibilities), Organization.Members, Analysis.Recommendations and
    // ReadabilityReport.Recommendations. Every getter returns _entries.AsReadOnly(), so EF reading
    // through the property gets a ReadOnlyCollection it cannot add to; the failure is an exception on
    // the first child insert, not at model build.
    [Fact]
    public void OwnedCollections_UseTheBackingField()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var collections = context.Model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetNavigations())
            .Where(navigation => navigation.IsCollection)
            .ToList();

        collections.Should().HaveCount(16);
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
        // absence would mean the whole value had stopped persisting.
        actual.Should().Equal(
            "Account", "Analysis", "Award", "Certificate", "ContactInformation", "Education",
            "Experience", "ImportSignals", "Interest", "JobPosting", "JobRequirement", "Language",
            "LanguageRequirement", "Membership", "Organization", "Project", "Publication",
            "ReadabilityBreakdown", "ReadabilityRecommendation", "ReadabilityReport", "Recommendation",
            "Reference", "RefreshToken", "Responsibility", "Resume", "ScoreBreakdown", "Skill");
    }

    private static IEnumerable<(IEntityType EntityType, IProperty Property)> EncryptedProperties(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetProperties().Select(property => (entityType, property)))
            .Where(entry => entry.property.FindAnnotation(PersistenceAnnotations.Encrypted)?.Value is true);

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

    private static string Name(IReadOnlyEntityType entityType) => entityType.ClrType.Name;
}
