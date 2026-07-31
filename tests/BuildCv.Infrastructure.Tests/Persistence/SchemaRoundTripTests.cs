using System.Text;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.Conventions;
using BuildCv.Infrastructure.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Tests.Persistence;

// Save, then reload in a brand-new context, against a real SQL Server running the committed
// migration. This is where a mapping that builds but cannot write shows up.
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SchemaRoundTripTests
{
    private readonly SqlServerFixture _fixture;

    public SchemaRoundTripTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Account_RoundTrips_WithDecryptedEmail()
    {
        var email = UniqueEmail("account");
        var account = NewAccount(email);

        await using (var context = _fixture.NewContext())
        {
            context.Accounts.Add(account);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.Accounts.SingleAsync(entity => entity.Id == account.Id);

        reloaded.Email.Value.Should().Be(email);
        reloaded.Password.Hash.Should().Be(account.Password.Hash);
        reloaded.Role.Should().Be(account.Role);
        reloaded.Status.Should().Be(account.Status);
        reloaded.FailedLoginCount.Should().Be(account.FailedLoginCount);
    }

    // The product requirement, checked at the only layer where it is true or false: the bytes on
    // disk. Reading the column back through EF proves the converter is symmetric, not that anything
    // was ever encrypted.
    [Fact]
    public async Task Account_StoresEmailAsCiphertext_AndItsBlindIndexAlongside()
    {
        var email = UniqueEmail("ciphertext");
        var account = NewAccount(email);

        await using (var context = _fixture.NewContext())
        {
            context.Accounts.Add(account);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var stored = await reader.Database
            .SqlQuery<byte[]>($"SELECT [Email] AS [Value] FROM [identity].[Accounts] WHERE [Id] = {account.Id.Value}")
            .SingleAsync();

        Encoding.UTF8.GetString(stored).Should().NotContain(email, "the address must not be readable in a dump");
        stored.Should().NotBeEquivalentTo(Encoding.UTF8.GetBytes(email));

        var storedHash = await reader.Database
            .SqlQuery<byte[]>($"SELECT [EmailHash] AS [Value] FROM [identity].[Accounts] WHERE [Id] = {account.Id.Value}")
            .SingleAsync();

        // The interceptor wrote it, and it is the digest the login path will look up by.
        storedHash.Should().Equal(new AccountEmailIndex(PersistenceTestContext.BlindIndex()).Compute(Email.Create(email)));
    }

    // The blind index is what makes an encrypted column findable, and a unique index over it is the
    // only thing that stops a concurrent duplicate registration.
    [Fact]
    public async Task Accounts_RejectASecondRowWithTheSameEmail()
    {
        var email = UniqueEmail("duplicate");

        await using (var context = _fixture.NewContext())
        {
            context.Accounts.Add(NewAccount(email));
            await context.SaveChangesAsync();
        }

        await using var second = _fixture.NewContext();
        second.Accounts.Add(NewAccount(email));

        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Resume_RoundTrips_WithEveryChildCollectionAndFullContactInformation()
    {
        var contact = new ContactInformation(
            PersonName.Create("Ada Lovelace"),
            Email.Create(UniqueEmail("resume")),
            PhoneNumber.Create("+541155551234"),
            "Buenos Aires, AR",
            Url.Create("https://ada.example.com"),
            "Analytical engine specialist.")
        {
            Profiles = [new Profile("GitHub", "ada", Url.Create("https://github.com/ada"))],
        };

        var resume = Resume.Create(AccountId.New(), contact);
        var period = DateRange.Create(new DateOnly(2020, 1, 1), new DateOnly(2023, 6, 30));

        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Analytical Engines SA"),
            "Principal Engineer",
            period,
            "Led the compiler team.")
        { Highlights = ["Shipped v1", "Halved build times"] });

        resume.AddEducation(new Education(
            OrganizationName.Create("University of London"), "BSc", "Mathematics", period, "First"));

        // `with`, not an object initializer: Skill is built by a factory method, and Keywords is an
        // init-only member on the record it returns.
        resume.AddSkill(Skill.Create(Technology.Create("C#"), SkillLevel.Expert, 10)
            with
        { Keywords = ["dotnet", "async"] });

        resume.AddProject(new Project(
            "Difference Engine",
            period,
            "A mechanical computer.",
            Url.Create("https://github.com/ada/difference-engine"),
            Url.Create("https://demo.example.com"))
        { Technologies = [Technology.Create("C#"), Technology.Create("SQL")], Highlights = ["Open sourced"] });

        resume.AddCertificate(new Certificate(
            "Azure Architect",
            OrganizationName.Create("Microsoft"),
            "CRED-123",
            Url.Create("https://learn.example.com/verify/CRED-123"),
            period));

        resume.AddLanguage(new Language("Spanish", "Native"));
        resume.AddAward(new Award("Turing Award", OrganizationName.Create("ACM"), new DateOnly(2022, 3, 1), "For services."));
        resume.AddPublication(new Publication(
            "Notes on the Engine",
            OrganizationName.Create("Scientific Memoirs"),
            Url.Create("https://papers.example.com/notes"),
            new DateOnly(1843, 10, 1),
            "The first algorithm."));
        resume.AddInterest(new Interest("Mathematics") { Keywords = ["number theory"] });
        resume.AddReference(new Reference(
            "Charles Babbage",
            "Professor",
            OrganizationName.Create("Cambridge"),
            Email.Create("charles@example.com"),
            PhoneNumber.Create("+441234567890"),
            "Highly recommended."));

        await using (var context = _fixture.NewContext())
        {
            context.Resumes.Add(resume);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.Resumes
            .Include(entity => entity.Experiences)
            .Include(entity => entity.Educations)
            .Include(entity => entity.Skills)
            .Include(entity => entity.Projects)
            .Include(entity => entity.Certificates)
            .Include(entity => entity.Languages)
            .Include(entity => entity.Awards)
            .Include(entity => entity.Publications)
            .Include(entity => entity.Interests)
            .Include(entity => entity.References)
            .SingleAsync(entity => entity.Id == resume.Id);

        // Structural, not record equality: ContactInformation.Profiles is an IReadOnlyList, and the
        // compiler-generated record equality compares that member by reference.
        reloaded.ContactInformation.Should().BeEquivalentTo(contact);

        reloaded.Experiences.Should().BeEquivalentTo(resume.Experiences);
        reloaded.Educations.Should().BeEquivalentTo(resume.Educations);
        reloaded.Skills.Should().BeEquivalentTo(resume.Skills);
        reloaded.Projects.Should().BeEquivalentTo(resume.Projects);
        reloaded.Certificates.Should().BeEquivalentTo(resume.Certificates);
        reloaded.Languages.Should().BeEquivalentTo(resume.Languages);
        reloaded.Awards.Should().BeEquivalentTo(resume.Awards);
        reloaded.Publications.Should().BeEquivalentTo(resume.Publications);
        reloaded.Interests.Should().BeEquivalentTo(resume.Interests);
        reloaded.References.Should().BeEquivalentTo(resume.References);
    }

    // The other half of the classification, proved rather than asserted about the model: the skill
    // name really is queryable in SQL. If Skill.Name were ever moved behind an envelope, this stops
    // matching and the failure names the feature that was lost.
    [Fact]
    public async Task Skills_AreQueryableAsPlaintext()
    {
        var skillName = $"Elixir-{Guid.NewGuid():N}";
        var resume = Resume.Create(AccountId.New(), MinimalContact("skills"));
        resume.AddSkill(Skill.Create(Technology.Create(skillName), SkillLevel.Advanced, 3));

        await using (var context = _fixture.NewContext())
        {
            context.Resumes.Add(resume);
            await context.SaveChangesAsync();
        }

        // Compares the whole value object. EF applies the Technology converter to the operand, which
        // is deterministic — the same string every time. That is precisely what an encrypted column
        // cannot do, and why this query would silently return nothing if Skill.Name were sealed.
        var technology = Technology.Create(skillName);

        await using var reader = _fixture.NewContext();
        var matches = await reader.Resumes
            .Where(entity => entity.Skills.Any(skill => skill.Name == technology))
            .Select(entity => entity.Id)
            .ToListAsync();

        matches.Should().ContainSingle().Which.Should().Be(resume.Id);
    }

    [Fact]
    public async Task JobPosting_RoundTrips_WithRequirementsAndResponsibilities()
    {
        var posting = JobPosting.Create(
            AccountId.New(), "Senior .NET Engineer", OrganizationName.Create("Contoso"), "Build things.");

        posting.SetRequirements(
        [
            JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave, 3),
            JobRequirement.Create(Technology.Create("SQL"), RequirementPriority.NiceToHave, 1),
        ]);
        posting.SetResponsibilities([Responsibility.Create("Ship features."), Responsibility.Create("Review code.")]);
        posting.Publish();

        await using (var context = _fixture.NewContext())
        {
            context.JobPostings.Add(posting);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.JobPostings
            .Include(entity => entity.Requirements)
            .Include(entity => entity.Responsibilities)
            .SingleAsync(entity => entity.Id == posting.Id);

        reloaded.Title.Should().Be(posting.Title);
        reloaded.Description.Should().Be(posting.Description);
        reloaded.CompanyName.Should().Be(posting.CompanyName);
        reloaded.Status.Should().Be(JobPostingStatus.Published);
        reloaded.Requirements.Should().BeEquivalentTo(posting.Requirements);
        reloaded.Responsibilities.Should().BeEquivalentTo(posting.Responsibilities);
    }

    [Fact]
    public async Task Organization_RoundTrips_WithMembers()
    {
        var organization = Organization.Create(
            OrganizationName.Create("Contoso"), Slug.Create($"contoso-{Guid.NewGuid():N}"), AccountId.New());
        organization.AddMember(AccountId.New(), MembershipRole.Admin);

        await using (var context = _fixture.NewContext())
        {
            context.Organizations.Add(organization);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.Organizations
            .Include(entity => entity.Members)
            .SingleAsync(entity => entity.Id == organization.Id);

        reloaded.Name.Should().Be(organization.Name);
        reloaded.Slug.Should().Be(organization.Slug);
        reloaded.Members.Should().BeEquivalentTo(organization.Members);
    }

    [Fact]
    public async Task Analysis_RoundTrips_WithBreakdownAndRecommendations()
    {
        var analysis = Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.5, ScoringWeightsSnapshot.Default()),
            ResumeId.New(),
            JobPostingId.New(),
            DateTimeOffset.UtcNow,
            ["Add more C# projects.", "Mention SQL explicitly."]);

        await using (var context = _fixture.NewContext())
        {
            context.Analyses.Add(analysis);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.Analyses.SingleAsync(entity => entity.Id == analysis.Id);

        reloaded.Breakdown.Should().Be(analysis.Breakdown);
        reloaded.Recommendations.Should().Equal(analysis.Recommendations);
        reloaded.OverallScore.Should().Be(analysis.OverallScore);
    }

    [Fact]
    public async Task RefreshToken_RoundTrips_AndIsFoundByItsBlindIndex()
    {
        var account = NewAccount(UniqueEmail("refresh"));
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var refreshToken = RefreshToken.Create(
            token, account.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));

        await using (var context = _fixture.NewContext())
        {
            context.Accounts.Add(account);
            context.RefreshTokens.Add(refreshToken);
            await context.SaveChangesAsync();
        }

        var digest = new RefreshTokenIndex(PersistenceTestContext.BlindIndex()).ComputeCandidates(token)[0];

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.RefreshTokens
            .SingleAsync(entity => EF.Property<byte[]>(entity, ShadowColumns.TokenHash) == digest);

        reloaded.Token.Should().Be(token);
        reloaded.AccountId.Should().Be(account.Id);
        reloaded.ExpiresAt.Should().BeCloseTo(refreshToken.ExpiresAt, TimeSpan.FromMilliseconds(1));
    }

    // Deleting an aggregate root tombstones it: the row survives for audit, and the global query
    // filter hides it from every read that did not ask for it.
    [Fact]
    public async Task DeletingARoot_TombstonesItInsteadOfRemovingTheRow()
    {
        var organization = Organization.Create(
            OrganizationName.Create("Fabrikam"), Slug.Create($"fabrikam-{Guid.NewGuid():N}"), AccountId.New());

        await using (var context = _fixture.NewContext())
        {
            context.Organizations.Add(organization);
            await context.SaveChangesAsync();
        }

        await using (var deleter = _fixture.NewContext())
        {
            deleter.Organizations.Remove(await deleter.Organizations.SingleAsync(e => e.Id == organization.Id));
            await deleter.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();

        (await reader.Organizations.AnyAsync(e => e.Id == organization.Id)).Should().BeFalse();
        (await reader.Organizations.IgnoreQueryFilters().AnyAsync(e => e.Id == organization.Id)).Should().BeTrue();
    }

    // Exercises the concurrency token AND, incidentally, proves an ordinary UPDATE does not try to
    // write the Seq IDENTITY column — EF only marks genuinely changed properties as modified.
    [Fact]
    public async Task StaleUpdate_IsRejectedByTheRowVersion()
    {
        var account = NewAccount(UniqueEmail("concurrency"));

        await using (var context = _fixture.NewContext())
        {
            context.Accounts.Add(account);
            await context.SaveChangesAsync();
        }

        await using var first = _fixture.NewContext();
        await using var second = _fixture.NewContext();

        var firstCopy = await first.Accounts.SingleAsync(entity => entity.Id == account.Id);
        var secondCopy = await second.Accounts.SingleAsync(entity => entity.Id == account.Id);

        firstCopy.ChangeRole(Role.Recruiter);
        await first.SaveChangesAsync();

        // Reads the row version it loaded BEFORE the first writer committed.
        secondCopy.ChangeRole(Role.Admin);
        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    // The audit columns are shadow state, so nothing in the Domain would reveal a silently broken
    // interceptor. Read them back explicitly.
    [Fact]
    public async Task Writes_StampTheActingPrincipalIntoTheAuditColumns()
    {
        var principal = AccountId.New();
        var organization = Organization.Create(
            OrganizationName.Create("Northwind"), Slug.Create($"northwind-{Guid.NewGuid():N}"), principal);

        await using (var context = _fixture.NewContext(new StubCurrentUser(principal)))
        {
            context.Organizations.Add(organization);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.Organizations.SingleAsync(entity => entity.Id == organization.Id);
        var entry = reader.Entry(reloaded);

        entry.Property<Guid?>(ShadowColumns.CreatedBy).CurrentValue.Should().Be(principal.Value);
        entry.Property<Guid?>(ShadowColumns.UpdatedBy).CurrentValue.Should().Be(principal.Value);
        entry.Property<DateTimeOffset?>(ShadowColumns.DeletedAt).CurrentValue.Should().BeNull();
        entry.Property<long>(ShadowColumns.Seq).CurrentValue.Should().BePositive("the keyset cursor is IDENTITY-assigned");
    }

    private sealed class StubCurrentUser(AccountId accountId) : ICurrentUser
    {
        public AccountId? AccountId { get; } = accountId;
    }

    private static Account NewAccount(string email) =>
        Account.Create(Email.Create(email), Password.Create(new PasswordHasher().Hash("correct-horse-battery")));

    private static ContactInformation MinimalContact(string label) =>
        new(PersonName.Create("Test Person"), Email.Create(UniqueEmail(label)));

    private static string UniqueEmail(string label) => $"{label}.{Guid.NewGuid():N}@example.com";
}
