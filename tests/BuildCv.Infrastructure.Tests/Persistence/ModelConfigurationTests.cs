using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
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
    };

    // The other half of the same requirement. Encrypting any of these would silently end a feature:
    // the scoring engine reads them, and internal analytics groups by them.
    private static readonly string[] ExpectedPlaintextColumns =
    [
        "Skill.Name", "Skill.Level", "Skill.YearsOfExperience", "Skill.Keywords",
        "Language.Name", "Language.Fluency",
        "Project.Technologies", "Project.Period",
        "Experience.Type", "Experience.Period",
        "Education.Period",
        "Certificate.ValidityPeriod",
        "Award.Date",
        "Publication.ReleaseDate",
        "JobRequirement.Skill", "JobRequirement.Priority", "JobRequirement.Weight",
        "JobPosting.Title", "JobPosting.Description", "JobPosting.CompanyName", "JobPosting.Status",
        "Organization.Name", "Organization.Slug", "Organization.Status",
        "Account.Role", "Account.Status", "Account.FailedLoginCount",
        "Analysis.ScoredAt", "Analysis.Recommendations",
        "ScoreBreakdown.SkillsScore", "ScoreBreakdown.Weights",
    ];

    private static readonly Type[] ExpectedAggregateRoots =
    [
        typeof(Account), typeof(RefreshToken), typeof(Resume),
        typeof(JobPosting), typeof(Organization), typeof(Analysis),
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

        foreach (var path in ExpectedPlaintextColumns)
        {
            FindProperty(context.Model, path).Should().NotBeNull(
                "{0} is expected to be a mapped, queryable column", path);
            encrypted.Should().NotContain(path,
                "{0} is analytical data the scoring engine and internal reporting have to query", path);
        }
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

    // The ten resume collections plus the three elsewhere. Every getter returns _entries.AsReadOnly(),
    // so EF reading through the property gets a ReadOnlyCollection it cannot add to; the failure is an
    // exception on the first child insert, not at model build.
    [Fact]
    public void OwnedCollections_UseTheBackingField()
    {
        using var context = PersistenceTestContext.ModelOnly();

        var collections = context.Model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetNavigations())
            .Where(navigation => navigation.IsCollection)
            .ToList();

        collections.Should().HaveCount(13);
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

        actual.Should().Equal(
            "Account", "Analysis", "Award", "Certificate", "ContactInformation", "Education",
            "Experience", "Interest", "JobPosting", "JobRequirement", "Language", "Membership",
            "Organization", "Project", "Publication", "Reference", "RefreshToken", "Responsibility",
            "Resume", "ScoreBreakdown", "Skill");
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
