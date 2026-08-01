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
using Microsoft.Data.SqlClient;
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

        // Asserting bare DbUpdateException would pass for the wrong reason: it is the base type for
        // ANY failed write, so a NOT NULL violation on EmailHash — which is what an unregistered
        // blind-index interceptor produces — would satisfy it. That is a separate regression, and it
        // must not be able to hide behind this test. 2627 is a unique CONSTRAINT violation, 2601 a
        // unique INDEX violation; EmailHash is the latter.
        var thrown = await act.Should().ThrowAsync<DbUpdateException>();

        thrown.Which.InnerException.Should().BeOfType<SqlException>()
            .Which.Number.Should().Be(2601, "the duplicate must be stopped by the unique index on EmailHash");
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

    // A tombstone must preserve the WHOLE aggregate, not just its root row.
    //
    // Owned navigations are eager-loaded, so loading a root always pulls its children into the change
    // tracker, where marking the root Deleted cascade-marks every one of them Deleted too. Converting
    // only the root back to a tombstone leaves those cascades standing, and SaveChanges then issues
    // real DELETEs for the children — a "soft" delete that destroys the entire aggregate except its
    // header. The test above passes happily while that happens, which is why these exist.
    [Fact]
    public async Task SoftDeletingAnOrganization_KeepsItsMemberships()
    {
        var founder = AccountId.New();
        var organization = Organization.Create(
            OrganizationName.Create("Fabrikam"), Slug.Create($"fabrikam-{Guid.NewGuid():N}"), founder);
        organization.AddMember(AccountId.New(), MembershipRole.Admin);

        await SaveThenSoftDelete(organization, (context, id) => context.Organizations.SingleAsync(e => e.Id == id), organization.Id);

        await using var reader = _fixture.NewContext();
        var tombstoned = await reader.Organizations
            .IgnoreQueryFilters()
            .Include(entity => entity.Members)
            .SingleAsync(entity => entity.Id == organization.Id);

        tombstoned.Members.Should().HaveCount(2, "a tombstoned organization still has to say who its owner was");
        tombstoned.Members.Should().Contain(member => member.AccountId == founder);
    }

    [Fact]
    public async Task SoftDeletingAJobPosting_KeepsItsRequirementsAndResponsibilities()
    {
        var posting = JobPosting.Create(
            AccountId.New(), "Doomed Posting", OrganizationName.Create("Contoso"), "Will be tombstoned.");
        posting.SetRequirements([JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave, 2)]);
        posting.SetResponsibilities([Responsibility.Create("Ship features.")]);

        await SaveThenSoftDelete(posting, (context, id) => context.JobPostings.SingleAsync(e => e.Id == id), posting.Id);

        await using var reader = _fixture.NewContext();
        var tombstoned = await reader.JobPostings
            .IgnoreQueryFilters()
            .Include(entity => entity.Requirements)
            .Include(entity => entity.Responsibilities)
            .SingleAsync(entity => entity.Id == posting.Id);

        tombstoned.Requirements.Should().ContainSingle();
        tombstoned.Responsibilities.Should().ContainSingle();
    }

    // The worst case of the four. Resume owns ten child tables plus a TABLE-SPLIT ContactInformation
    // whose Contact_* columns are NOT NULL — a cascaded delete of that owned reference against a
    // merely-modified principal cannot even be written. All ten collections are populated, so the
    // name does not claim more than the body checks.
    [Fact]
    public async Task SoftDeletingAResume_KeepsAllTenChildCollectionsAndItsContactInformation()
    {
        var contact = new ContactInformation(
            PersonName.Create("Grace Hopper"), Email.Create(UniqueEmail("tombstone")));
        var resume = Resume.Create(AccountId.New(), contact);
        var period = DateRange.Create(new DateOnly(2021, 1, 1), null);

        resume.AddExperience(new Experience(
            ExperienceType.Professional, OrganizationName.Create("Navy"), "Rear Admiral", period));
        resume.AddEducation(new Education(
            OrganizationName.Create("Yale"), "PhD", "Mathematics", period, "Summa"));
        resume.AddSkill(Skill.Create(Technology.Create("COBOL"), SkillLevel.Expert, 30));
        resume.AddProject(new Project("A-0 Compiler", period, "The first compiler."));
        resume.AddCertificate(new Certificate(
            "Naval Reserve", OrganizationName.Create("US Navy"), "NR-1", null, period));
        resume.AddLanguage(new Language("English", "Native"));
        resume.AddAward(new Award("Medal of Technology", OrganizationName.Create("USA"), new DateOnly(1991, 1, 1), null));
        resume.AddPublication(new Publication(
            "Compiling Routines", OrganizationName.Create("ACM"), null, new DateOnly(1952, 1, 1), null));
        resume.AddInterest(new Interest("Teaching") { Keywords = ["compilers"] });
        resume.AddReference(new Reference("Howard Aiken", "Professor", null, null, null, "Recommended."));

        await SaveThenSoftDelete(resume, (context, id) => context.Resumes.SingleAsync(e => e.Id == id), resume.Id);

        await using var reader = _fixture.NewContext();
        var tombstoned = await reader.Resumes
            .IgnoreQueryFilters()
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

        // The table-split owned reference: NOT NULL columns on the principal's own row.
        tombstoned.ContactInformation.FullName.Value.Should().Be("Grace Hopper");

        tombstoned.Experiences.Should().ContainSingle();
        tombstoned.Educations.Should().ContainSingle();
        tombstoned.Skills.Should().ContainSingle();
        tombstoned.Projects.Should().ContainSingle();
        tombstoned.Certificates.Should().ContainSingle();
        tombstoned.Languages.Should().ContainSingle();
        tombstoned.Awards.Should().ContainSingle();
        tombstoned.Publications.Should().ContainSingle();
        tombstoned.Interests.Should().ContainSingle();
        tombstoned.References.Should().ContainSingle();
    }

    // The fifth root, and the second table-split owned reference in the model. ScoreBreakdown's five
    // score columns and its Weights column are all IsRequired(), and Navigation(a => a.Breakdown) is
    // required too — the identical shape that produced the Contact_Email NOT NULL crash. Nothing
    // pinned it until now.
    [Fact]
    public async Task SoftDeletingAnAnalysis_KeepsItsScoreBreakdown()
    {
        var analysis = Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.5, ScoringWeightsSnapshot.Default()),
            ResumeId.New(),
            JobPostingId.New(),
            DateTimeOffset.UtcNow,
            ["Add more C# projects."]);

        await SaveThenSoftDelete(analysis, (context, id) => context.Analyses.SingleAsync(e => e.Id == id), analysis.Id);

        await using var reader = _fixture.NewContext();
        var tombstoned = await reader.Analyses
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == analysis.Id);

        tombstoned.Breakdown.Should().Be(analysis.Breakdown);
        tombstoned.Recommendations.Should().Equal(analysis.Recommendations);
    }

    // The other side of the same rule, and the reason the fix has to be scoped to the root being
    // tombstoned rather than to every cascaded owned entry. Taking a skill off a LIVE resume is not a
    // tombstone — it is a smaller resume, and that row must really go.
    [Fact]
    public async Task RemovingAChildFromALiveRoot_StillHardDeletesTheChildRow()
    {
        var resume = Resume.Create(AccountId.New(), MinimalContact("child-removal"));
        resume.AddSkill(Skill.Create(Technology.Create("Fortran"), SkillLevel.Advanced, 5));
        resume.AddSkill(Skill.Create(Technology.Create("Ada"), SkillLevel.Beginner, 1));

        await using (var context = _fixture.NewContext())
        {
            context.Resumes.Add(resume);
            await context.SaveChangesAsync();
        }

        await using (var mutator = _fixture.NewContext())
        {
            var tracked = await mutator.Resumes.Include(e => e.Skills).SingleAsync(e => e.Id == resume.Id);
            tracked.RemoveSkill("Fortran");
            await mutator.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.Resumes.Include(e => e.Skills).SingleAsync(e => e.Id == resume.Id);

        reloaded.Skills.Should().ContainSingle().Which.Name.Name.Should().Be("Ada");

        // Gone from the table entirely — child rows carry no DeletedAt to be hidden by.
        var remaining = await reader.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [resumes].[Skills] WHERE [ResumeId] = {resume.Id.Value}")
            .SingleAsync();
        remaining.Should().Be(1);
    }

    // THE test for the scoping decision, and the only one that actually runs that code path with
    // something to discriminate on. The three tombstone tests above only prove children survive; the
    // hard-delete test above only proves a child dies while its root LIVES. Neither puts a root into
    // Deleted alongside a genuinely removed child, so neither can tell the navigation traversal apart
    // from a blanket "restore every Deleted owned entry" scan — which would resurrect the removed row.
    //
    // Verified by swapping the implementation for that blanket scan: this test fails, the other four
    // still pass.
    [Fact]
    public async Task SoftDeletingARootWhileRemovingOneChild_TombstonesTheRootAndStillDropsThatChild()
    {
        var resume = Resume.Create(AccountId.New(), MinimalContact("combined"));
        resume.AddSkill(Skill.Create(Technology.Create("Fortran"), SkillLevel.Advanced, 5));
        resume.AddSkill(Skill.Create(Technology.Create("Ada"), SkillLevel.Beginner, 1));
        resume.AddSkill(Skill.Create(Technology.Create("Lisp"), SkillLevel.Intermediate, 3));

        await using (var context = _fixture.NewContext())
        {
            context.Resumes.Add(resume);
            await context.SaveChangesAsync();
        }

        // One unit of work: drop a skill, then tombstone the resume that owned it.
        await using (var mutator = _fixture.NewContext())
        {
            var tracked = await mutator.Resumes.Include(entity => entity.Skills)
                .SingleAsync(entity => entity.Id == resume.Id);

            tracked.RemoveSkill("Fortran");
            mutator.Resumes.Remove(tracked);
            await mutator.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();

        // The root is tombstoned, not gone.
        (await reader.Resumes.AnyAsync(entity => entity.Id == resume.Id)).Should().BeFalse();
        (await reader.Resumes.IgnoreQueryFilters().AnyAsync(entity => entity.Id == resume.Id)).Should().BeTrue();

        // Raw SQL, because child rows have no DeletedAt and no query filter to see through: the two
        // survivors are really on disk, and the removed one is really not.
        var surviving = await reader.Database
            .SqlQuery<string>(
                $"SELECT [Name] AS [Value] FROM [resumes].[Skills] WHERE [ResumeId] = {resume.Id.Value}")
            .ToListAsync();

        surviving.Should().BeEquivalentTo(["Ada", "Lisp"],
            "the cascade on the tombstoned root must be undone for the children it still owns, "
            + "and NOT for the one that was deliberately removed from it");
    }

    private async Task SaveThenSoftDelete<TRoot, TId>(
        TRoot root, Func<Infrastructure.Persistence.BuildCvDbContext, TId, Task<TRoot>> load, TId id)
        where TRoot : class
    {
        await using (var context = _fixture.NewContext())
        {
            context.Add(root);
            await context.SaveChangesAsync();
        }

        await using var deleter = _fixture.NewContext();
        deleter.Remove(await load(deleter, id));
        await deleter.SaveChangesAsync();
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
