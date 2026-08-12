using System.Text;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Readability;
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
            OrganizationName.Create("University of London"), "BSc", "Mathematics", period, "First",
            EducationLevel.Bachelor));

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

        resume.AddLanguage(Language.Create("Spanish", "Native", LanguageProficiency.Native));
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

        // The two new level columns, named where a failure says which one broke. Each sits beside free
        // text that says roughly the same thing in prose, and the two are classified differently on
        // purpose: the LEVEL is plaintext because it is the closed, comparable value the engine reads,
        // and the prose beside it — Education.Degree, Language.Fluency — is sealed because it is a
        // sentence a person wrote about themselves. A level lost on the way to disk would leave only
        // prose behind, which the engine must never parse.
        reloaded.Languages.Single().Level.Should().Be(LanguageProficiency.Native);
        reloaded.Educations.Single().Level.Should().Be(EducationLevel.Bachelor);

        // THE ORDINARY CASE for the optional owned value: this resume was built by hand, so all four
        // Import_* columns are null and EF must materialize the whole value as null rather than as an
        // ImportSignals full of defaults. A default-valued instance would read as
        // (Unknown, HadTextLayer: false) — a document nobody uploaded, scored as unparseable.
        reloaded.ImportSignals.Should().BeNull();
    }

    // The other half of the same value, against a real database: every field of an imported resume's
    // signals survives the round trip, INCLUDING the two whose absence is meaningful. A null page count
    // must not come back as zero, and ImportWarningFlags.None must not come back as anything else — both
    // are what the ATS-parseability rule reads, and both are invisible to a test that only checks the
    // value is non-null.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Resume_RoundTrips_ItsImportSignals()
    {
        var signals = ImportSignals.Create(
            ColumnLayout.Multiple, hadTextLayer: false, pageCount: 4, ImportWarningFlags.NoTextContent);

        var resume = Resume.Create(
            AccountId.New(),
            new ContactInformation(PersonName.Create("Ada Lovelace"), Email.Create(UniqueEmail("signals"))),
            signals);

        await using (var context = _fixture.NewContext())
        {
            context.Resumes.Add(resume);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.Resumes.SingleAsync(entity => entity.Id == resume.Id);

        reloaded.ImportSignals.Should().Be(signals);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Resume_RoundTrips_ImportSignalsWithNoPageCountAndNoWarnings()
    {
        var signals = ImportSignals.Create(ColumnLayout.Unknown, hadTextLayer: true);

        var resume = Resume.Create(
            AccountId.New(),
            new ContactInformation(PersonName.Create("Ada Lovelace"), Email.Create(UniqueEmail("signals2"))),
            signals);

        await using (var context = _fixture.NewContext())
        {
            context.Resumes.Add(resume);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.Resumes.SingleAsync(entity => entity.Id == resume.Id);

        // NOT null, even though three of the four columns hold their zero value and the fourth is null.
        // This is the case an optional owned type gets wrong in the dangerous direction: EF decides the
        // value is absent from the columns alone, and a mapping that leaned on "Unknown means nothing
        // was uploaded" would silently drop the evidence for every DOCX and pasted-text import.
        reloaded.ImportSignals.Should().NotBeNull();
        reloaded.ImportSignals.Should().Be(signals);
    }

    // The Language.Fluency ruling, checked at the only layer where it is true or false: the bytes on
    // disk. BeEquivalentTo in the round-trip test above reads the column back THROUGH the converter,
    // which a passthrough would satisfy — it proves the conversion is symmetric, not that anything was
    // ever encrypted. Same shape as Account_StoresEmailAsCiphertext.
    //
    // The plaintext is deliberately the kind of sentence the ruling is about: prose a candidate wrote
    // about themselves that happens to sit in a field named after a level.
    //
    // Level and Name are asserted in the SAME statement, read raw, because they are what the
    // encryption is traded against. Sealing either would leave the engine with only the prose it is
    // forbidden to parse, and this test would then be pinning half a classification.
    [Fact]
    public async Task Resume_StoresLanguageFluencyAsCiphertext_ButKeepsItsNameAndLevelQueryable()
    {
        var fluency = $"nativo, aprendido de mi abuela colombiana-{Guid.NewGuid():N}";
        var resume = Resume.Create(AccountId.New(), MinimalContact("fluency"));
        resume.AddLanguage(Language.Create("Español", fluency, LanguageProficiency.Native));

        await using (var context = _fixture.NewContext())
        {
            context.Resumes.Add(resume);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var stored = await reader.Database
            .SqlQuery<byte[]>(
                $"SELECT [Fluency] AS [Value] FROM [resumes].[Languages] WHERE [ResumeId] = {resume.Id.Value}")
            .SingleAsync();

        Encoding.UTF8.GetString(stored).Should().NotContain(fluency,
            "a sentence a candidate wrote about themselves must not be readable in a dump");
        Encoding.Unicode.GetString(stored).Should().NotContain(fluency,
            "an ALTERed nvarchar column would leave exactly these UTF-16 bytes behind");
        stored.Should().NotBeEquivalentTo(Encoding.UTF8.GetBytes(fluency));

        // The plaintext half, read raw rather than through EF, so it cannot be satisfied by a
        // converter. Level is a tinyint the scoring engine compares; Name carries the index it joins on.
        var level = await reader.Database
            .SqlQuery<byte>(
                $"SELECT [Level] AS [Value] FROM [resumes].[Languages] WHERE [ResumeId] = {resume.Id.Value}")
            .SingleAsync();
        level.Should().Be((byte)LanguageProficiency.Native);

        var name = await reader.Database
            .SqlQuery<string>(
                $"SELECT [Name] AS [Value] FROM [resumes].[Languages] WHERE [ResumeId] = {resume.Id.Value}")
            .SingleAsync();
        name.Should().Be("Español");

        // And it still comes back. An envelope that cannot be reopened under its own AAD would leave
        // the assertions above passing while the feature was gone.
        var reloadedResume = await reader.Resumes
            .Include(entity => entity.Languages)
            .SingleAsync(entity => entity.Id == resume.Id);
        reloadedResume.Languages.Single().Fluency.Should().Be(fluency);
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
    public async Task JobPosting_RoundTrips_WithRequirementsLanguagesAndResponsibilities()
    {
        var posting = JobPosting.Create(
            AccountId.New(), "Senior .NET Engineer", OrganizationName.Create("Contoso"), "Build things.");

        posting.SetRequirements(
        [
            JobRequirement.Create(Technology.Create("C#"), RequirementPriority.MustHave, 3),
            JobRequirement.Create(Technology.Create("SQL"), RequirementPriority.NiceToHave, 1),
        ]);
        posting.SetLanguageRequirements(
        [
            LanguageRequirement.Create("English", LanguageProficiency.Professional),
            LanguageRequirement.Create("Español", LanguageProficiency.Native),
        ]);
        posting.SetResponsibilities([Responsibility.Create("Ship features."), Responsibility.Create("Review code.")]);
        posting.SetEducationLevel(EducationLevel.Bachelor);
        posting.Publish();

        await using (var context = _fixture.NewContext())
        {
            context.JobPostings.Add(posting);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.JobPostings
            .Include(entity => entity.Requirements)
            .Include(entity => entity.LanguageRequirements)
            .Include(entity => entity.Responsibilities)
            .SingleAsync(entity => entity.Id == posting.Id);

        reloaded.Title.Should().Be(posting.Title);
        reloaded.Description.Should().Be(posting.Description);
        reloaded.CompanyName.Should().Be(posting.CompanyName);
        reloaded.Status.Should().Be(JobPostingStatus.Published);
        reloaded.EducationLevel.Should().Be(EducationLevel.Bachelor);
        reloaded.Requirements.Should().BeEquivalentTo(posting.Requirements);
        reloaded.LanguageRequirements.Should().BeEquivalentTo(posting.LanguageRequirements);
        reloaded.Responsibilities.Should().BeEquivalentTo(posting.Responsibilities);

        // The classification, proved rather than asserted about the model: both columns really are
        // MATCHABLE in SQL. Translation is not the signal and never was — an encrypted column
        // translates perfectly well and then silently returns nothing, because the operand is sealed
        // under a fresh nonce. What this asserts is that the rows come back. Seal either column and
        // this stops matching, and the failure names the feature that was lost: PR 3 needs exactly
        // this question — "postings requiring Spanish at native level" — and BeEquivalentTo above
        // cannot show it, because it only reads bytes back through the converter.
        var demanding = await reader.JobPostings
            .Where(entity => entity.LanguageRequirements.Any(requirement =>
                requirement.Name == "Español" && requirement.MinimumLevel == LanguageProficiency.Native))
            .Select(entity => entity.Id)
            .ToListAsync();

        demanding.Should().Contain(posting.Id);

        // The null direction, through EF rather than only through the Domain. The nullable column
        // makes this safe today, so it is coverage rather than a defect — but "stated, then
        // withdrawn" is the direction that silently degrades to 0 if a signature ever stops being
        // nullable, and 0 reads as HighSchool: a requirement the posting no longer makes.
        await using (var editor = _fixture.NewContext())
        {
            var tracked = await editor.JobPostings.SingleAsync(entity => entity.Id == posting.Id);
            tracked.SetEducationLevel(null);
            await editor.SaveChangesAsync();
        }

        await using var afterClearing = _fixture.NewContext();
        (await afterClearing.JobPostings.SingleAsync(entity => entity.Id == posting.Id))
            .EducationLevel.Should().BeNull();
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
            ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.5, 0.4, ScoringWeightsSnapshot.Default()),
            ResumeId.New(),
            JobPostingId.New(),
            DateTimeOffset.UtcNow,
            NewRecommendations());

        await using (var context = _fixture.NewContext())
        {
            context.Analyses.Add(analysis);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.Analyses
            .Include(entity => entity.Recommendations)
            .SingleAsync(entity => entity.Id == analysis.Id);

        reloaded.Breakdown.Should().Be(analysis.Breakdown);
        reloaded.Breakdown.LanguagesScore.Should().Be(0.4, "the sixth section has a column of its own");
        reloaded.Breakdown.Weights.Should().Be(ScoringWeightsSnapshot.Default(),
            "the six-member snapshot has to survive the JSON column intact");

        // BeEquivalentTo rather than Equal: Recommendations is a SET on disk with a surrogate key and
        // no stored position, so the reload order is the server's to choose.
        reloaded.Recommendations.Should().BeEquivalentTo(analysis.Recommendations);
        reloaded.OverallScore.Should().Be(analysis.OverallScore);
    }

    // The classification for the one encrypted column on this aggregate, checked where it is true or
    // false: the bytes on disk. Reading it back through EF only proves the converter is symmetric.
    //
    // The structure beside it must stay READABLE, and that is asserted in the same statement — sealing
    // Kind or Section would end "which advice do we give most often", which is the question the
    // encrypted Message is traded against.
    [Fact]
    public async Task Analysis_StoresRecommendationMessagesAsCiphertext_AndTheirStructureInPlaintext()
    {
        var message = $"Mention SQL explicitly-{Guid.NewGuid():N}";
        var analysis = Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.5, 0.4, ScoringWeightsSnapshot.Default()),
            ResumeId.New(),
            JobPostingId.New(),
            DateTimeOffset.UtcNow,
            [
                Recommendation.Create(
                    SectionType.Skills, RecommendationPriority.Critical,
                    RecommendationKind.MissingMustHaveSkill, message, 0.45),
            ]);

        await using (var context = _fixture.NewContext())
        {
            context.Analyses.Add(analysis);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var stored = await reader.Database
            .SqlQuery<byte[]>(
                $"SELECT [Message] AS [Value] FROM [scoring].[Recommendations] WHERE [AnalysisId] = {analysis.Id.Value}")
            .SingleAsync();

        Encoding.UTF8.GetString(stored).Should().NotContain(message, "the sentence quotes the candidate's resume");
        stored.Should().NotBeEquivalentTo(Encoding.UTF8.GetBytes(message));

        // Plaintext tinyints, queryable by the rollup the (Section, Priority) index exists for.
        var kind = await reader.Database
            .SqlQuery<byte>(
                $"SELECT [Kind] AS [Value] FROM [scoring].[Recommendations] WHERE [AnalysisId] = {analysis.Id.Value}")
            .SingleAsync();

        kind.Should().Be((byte)RecommendationKind.MissingMustHaveSkill);
    }

    // The readability aggregate, against the real engine running the committed migration. This is where
    // a mapping that BUILDS but cannot write shows up — a new schema, two new tables and a JSON weights
    // column, none of which ModelConfigurationTests can execute.
    [Fact]
    public async Task ReadabilityReport_RoundTrips_WithBreakdownAndRecommendations()
    {
        var report = ReadabilityReport.Create(
            ReadabilityReportId.New(),
            ReadabilityBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.0, ReadabilityWeightsSnapshot.Default()),
            ResumeId.New(),
            DateTimeOffset.UtcNow,
            NewReadabilityRecommendations());

        await using (var context = _fixture.NewContext())
        {
            context.ReadabilityReports.Add(report);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.ReadabilityReports
            .Include(entity => entity.Recommendations)
            .SingleAsync(entity => entity.Id == report.Id);

        reloaded.Breakdown.Should().Be(report.Breakdown);
        reloaded.Breakdown.AtsParseabilityScore.Should().Be(0.0, "the fifth section has a column of its own");
        reloaded.Breakdown.Weights.Should().Be(ReadabilityWeightsSnapshot.Default(),
            "the five-member snapshot has to survive the JSON column intact");
        reloaded.ResumeId.Should().Be(report.ResumeId);

        // BeEquivalentTo rather than Equal: Recommendations is a SET on disk with a surrogate key and no
        // stored position, so the reload order is the server's to choose.
        reloaded.Recommendations.Should().BeEquivalentTo(report.Recommendations);
        reloaded.ReadabilityScore.Should().Be(report.ReadabilityScore);
        reloaded.Band.Should().Be(report.Band);
    }

    // The classification for the one encrypted column on this aggregate, checked where it is true or
    // false: the bytes on disk. Reading it back through EF only proves the converter is symmetric.
    //
    // The structure beside it must stay READABLE, and that is asserted in the same statement — sealing
    // Kind or Section would end "which advice do we give most often", which is the question the
    // encrypted Message is traded against.
    [Fact]
    public async Task ReadabilityReport_StoresRecommendationMessagesAsCiphertext_AndTheirStructureInPlaintext()
    {
        var message = $"Add an entry covering the 14-month gap before 'Backend Developer'-{Guid.NewGuid():N}";
        var report = ReadabilityReport.Create(
            ReadabilityReportId.New(),
            ReadabilityBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.0, ReadabilityWeightsSnapshot.Default()),
            ResumeId.New(),
            DateTimeOffset.UtcNow,
            [
                ReadabilityRecommendation.Create(
                    ReadabilitySectionType.Chronology, RecommendationPriority.Critical,
                    ReadabilityRecommendationKind.UnexplainedEmploymentGap, message, 0.15),
            ]);

        await using (var context = _fixture.NewContext())
        {
            context.ReadabilityReports.Add(report);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var stored = await reader.Database
            .SqlQuery<byte[]>(
                $"SELECT [Message] AS [Value] FROM [readability].[Recommendations] WHERE [ReadabilityReportId] = {report.Id.Value}")
            .SingleAsync();

        Encoding.UTF8.GetString(stored).Should().NotContain(message, "the sentence quotes the candidate's resume");
        stored.Should().NotBeEquivalentTo(Encoding.UTF8.GetBytes(message));

        // Plaintext tinyints, queryable by the rollup the (Section, Priority) index exists for.
        var kind = await reader.Database
            .SqlQuery<byte>(
                $"SELECT [Kind] AS [Value] FROM [readability].[Recommendations] WHERE [ReadabilityReportId] = {report.Id.Value}")
            .SingleAsync();

        kind.Should().Be((byte)ReadabilityRecommendationKind.UnexplainedEmploymentGap);
    }

    // TWO CONTEXT STRINGS, ONE ARGUMENT, executed at the layer where it is true or false. The AAD binds
    // an envelope to the column it was written for, so an envelope lifted out of scoring.Recommendations
    // and dropped into readability.Recommendations must FAIL to decrypt. Sharing "Recommendation.Message"
    // between the two tables would make that move silent, and no model-shape assertion could see it —
    // the annotation would be identical either way.
    [Fact]
    public async Task AMessageEnvelopeMovedBetweenTheTwoRecommendationTables_FailsToDecrypt()
    {
        var message = $"Add more C# projects-{Guid.NewGuid():N}";
        var analysis = Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.5, 0.4, ScoringWeightsSnapshot.Default()),
            ResumeId.New(),
            JobPostingId.New(),
            DateTimeOffset.UtcNow,
            [
                Recommendation.Create(
                    SectionType.Projects, RecommendationPriority.Important,
                    RecommendationKind.FewerProjectsThanExpected, message, 0.05),
            ]);

        var report = ReadabilityReport.Create(
            ReadabilityReportId.New(),
            ReadabilityBreakdown.Create(0.5, 0.5, 0.5, 0.5, 0.0, ReadabilityWeightsSnapshot.Default()),
            ResumeId.New(),
            DateTimeOffset.UtcNow,
            [
                ReadabilityRecommendation.Create(
                    ReadabilitySectionType.Contact, RecommendationPriority.Important,
                    ReadabilityRecommendationKind.NoPhoneNumber, "Add a phone number.", 0.05),
            ]);

        await using (var context = _fixture.NewContext())
        {
            context.Analyses.Add(analysis);
            context.ReadabilityReports.Add(report);
            await context.SaveChangesAsync();
        }

        await using (var mover = _fixture.NewContext())
        {
            await mover.Database.ExecuteSqlAsync(
                $"""
                UPDATE readability.Recommendations
                SET [Message] = (
                    SELECT TOP 1 [Message] FROM scoring.Recommendations WHERE [AnalysisId] = {analysis.Id.Value})
                WHERE [ReadabilityReportId] = {report.Id.Value}
                """);
        }

        await using var reader = _fixture.NewContext();
        var act = async () => await reader.ReadabilityReports
            .Include(entity => entity.Recommendations)
            .SingleAsync(entity => entity.Id == report.Id);

        await act.Should().ThrowAsync<Exception>(
            "the envelope is authenticated against the column path it was sealed under");
    }

    // The candidates schema, against a real SQL Server running the committed migration: a new schema,
    // eleven new tables, and ten owned collections mapped by a class SHARED with Resume. This is where a
    // mapping that builds but cannot write shows up, and none of it is reachable from
    // ModelConfigurationTests — that file asserts the model's shape without ever opening a connection.
    [Fact]
    public async Task CandidateProfile_RoundTrips_WithItsCollectionsAndFullContactInformation()
    {
        var contact = new ContactInformation(
            PersonName.Create("Grace Hopper"),
            Email.Create(UniqueEmail("profile")),
            PhoneNumber.Create("+541155559876"),
            "Buenos Aires, AR",
            Url.Create("https://grace.example.com"),
            "Compiler pioneer.")
        {
            Profiles = [new Profile("GitHub", "grace", Url.Create("https://github.com/grace"))],
        };

        var profile = CandidateProfile.Create(AccountId.New(), contact);
        var period = DateRange.Create(new DateOnly(2018, 3, 1), new DateOnly(2024, 2, 29));

        profile.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Remington Rand"),
            "Systems Engineer",
            period,
            "Wrote the first compiler.")
        { Highlights = ["Shipped A-0", "Coined 'bug'"] });

        profile.AddEducation(new Education(
            OrganizationName.Create("Yale"), "PhD", "Mathematics", period, "Summa cum laude",
            EducationLevel.Doctorate));

        profile.AddSkill(Skill.Create(Technology.Create("COBOL"), SkillLevel.Expert, 30)
            with
        { Keywords = ["compilers", "mainframe"] });

        profile.AddLanguage(Language.Create("English", "Native tongue", LanguageProficiency.Native));
        profile.AddInterest(new Interest("Sailing") { Keywords = ["navigation"] });

        await using (var context = _fixture.NewContext())
        {
            context.CandidateProfiles.Add(profile);
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.NewContext();
        var reloaded = await reader.CandidateProfiles
            .AsSplitQuery()
            .SingleAsync(entity => entity.Id == profile.Id);

        reloaded.OwnerId.Should().Be(profile.OwnerId);

        // BeEquivalentTo, not Be: ContactInformation is a record holding a LIST of profiles, and a
        // record's generated equality compares that list by reference. Be() would fail on a value that
        // round-tripped perfectly.
        reloaded.ContactInformation.Should().BeEquivalentTo(contact);

        reloaded.Experiences.Should().BeEquivalentTo(profile.Experiences);
        reloaded.Educations.Should().BeEquivalentTo(profile.Educations);
        reloaded.Skills.Should().BeEquivalentTo(profile.Skills);
        reloaded.Interests.Should().BeEquivalentTo(profile.Interests);

        // The mixed collection, both halves at once: Fluency rides back through its envelope while Level
        // and Name stayed queryable beside it. Asserting only the reload would pass with the whole row
        // sealed, which is the classification this schema shares with the resumes tables.
        reloaded.Languages.Should().BeEquivalentTo(profile.Languages);
        var level = await reader.Database
            .SqlQuery<byte>(
                $"SELECT [Level] AS [Value] FROM [candidates].[Languages] WHERE [CandidateProfileId] = {profile.Id.Value}")
            .SingleAsync();
        level.Should().Be((byte)LanguageProficiency.Native);
    }

    // ONE PROFILE PER ACCOUNT, enforced where it is actually true. A check in a handler would let two
    // concurrent imports both read "no profile yet" and both insert, and the loser of that race is a
    // second copy of the candidate's entire history that nothing reconciles.
    [Fact]
    public async Task CandidateProfiles_RejectASecondProfileForOneAccount()
    {
        var owner = AccountId.New();

        await using (var context = _fixture.NewContext())
        {
            context.CandidateProfiles.Add(CandidateProfile.Create(owner, MinimalContact("first")));
            await context.SaveChangesAsync();
        }

        await using var second = _fixture.NewContext();
        second.CandidateProfiles.Add(CandidateProfile.Create(owner, MinimalContact("second")));

        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>(
            "the filtered unique index on OwnerId is what makes one-profile-per-account true");
    }

    // THE AAD PREFIX, EXECUTED — the same argument as the two recommendation tables above, applied to the
    // pair this refactor created. CvItemCollections maps the resume's ten item collections AND the
    // profile's, and the CLASSIFICATION is deliberately shared; the AAD deliberately is not. The
    // profile's columns seal under "CandidateProfile.Experience.Organization", the resume's under
    // "Experience.Organization", so an employer's name lifted out of resumes.Experiences and dropped into
    // candidates.Experiences must FAIL to decrypt.
    //
    // Nothing about the model can see this: one shared context string would leave the annotation
    // identical, the converter symmetric and every shape assertion green, while the two tables quietly
    // became one envelope namespace.
    [Fact]
    public async Task AnOrganizationEnvelopeMovedFromAResumeIntoAProfile_FailsToDecrypt()
    {
        var period = DateRange.Create(new DateOnly(2021, 1, 1), new DateOnly(2022, 1, 1));

        var resume = Resume.Create(AccountId.New(), MinimalContact("cv"));
        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create($"Globant-{Guid.NewGuid():N}"),
            "Backend Developer",
            period));

        var profile = CandidateProfile.Create(AccountId.New(), MinimalContact("master"));
        profile.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Mercado Libre"),
            "Backend Developer",
            period));

        await using (var context = _fixture.NewContext())
        {
            context.Resumes.Add(resume);
            context.CandidateProfiles.Add(profile);
            await context.SaveChangesAsync();
        }

        await using (var mover = _fixture.NewContext())
        {
            await mover.Database.ExecuteSqlAsync(
                $"""
                UPDATE candidates.Experiences
                SET [Organization] = (
                    SELECT TOP 1 [Organization] FROM resumes.Experiences WHERE [ResumeId] = {resume.Id.Value})
                WHERE [CandidateProfileId] = {profile.Id.Value}
                """);
        }

        await using var reader = _fixture.NewContext();
        var act = async () => await reader.CandidateProfiles
            .AsSplitQuery()
            .SingleAsync(entity => entity.Id == profile.Id);

        await act.Should().ThrowAsync<Exception>(
            "the envelope is authenticated against the column path it was sealed under, and these two "
            + "columns hold the same kind of value in two different tables");
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
        resume.AddLanguage(Language.Create("English", "Native"));
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

    // The fifth root, and the second table-split owned reference in the model. ScoreBreakdown's six
    // score columns and its Weights column are all IsRequired(), and Navigation(a => a.Breakdown) is
    // required too — the identical shape that produced the Contact_Email NOT NULL crash. Nothing
    // pinned it until now.
    //
    // Analysis now also owns a child TABLE, which puts it in the same category as the three tombstone
    // tests above: loading the root pulls the recommendations into the change tracker, where marking
    // the root Deleted cascade-marks every one of them Deleted too. A tombstone that kept the header
    // and issued real DELETEs for the advice would leave a score no one can explain.
    [Fact]
    public async Task SoftDeletingAnAnalysis_KeepsItsScoreBreakdownAndItsRecommendations()
    {
        var analysis = Analysis.Create(
            AnalysisId.New(),
            ScoreBreakdown.Create(0.9, 0.8, 0.7, 0.6, 0.5, 0.4, ScoringWeightsSnapshot.Default()),
            ResumeId.New(),
            JobPostingId.New(),
            DateTimeOffset.UtcNow,
            NewRecommendations());

        await SaveThenSoftDelete(analysis, (context, id) => context.Analyses.SingleAsync(e => e.Id == id), analysis.Id);

        await using var reader = _fixture.NewContext();
        var tombstoned = await reader.Analyses
            .IgnoreQueryFilters()
            .Include(entity => entity.Recommendations)
            .SingleAsync(entity => entity.Id == analysis.Id);

        tombstoned.Breakdown.Should().Be(analysis.Breakdown);
        tombstoned.Recommendations.Should().BeEquivalentTo(analysis.Recommendations);

        // Raw, because child rows carry no DeletedAt and no query filter to see through: the advice is
        // really still on disk rather than merely still in the change tracker.
        var surviving = await reader.Database
            .SqlQuery<int>(
                $"SELECT COUNT(*) AS [Value] FROM [scoring].[Recommendations] WHERE [AnalysisId] = {analysis.Id.Value}")
            .SingleAsync();

        surviving.Should().Be(2);
    }

    private static IReadOnlyList<Recommendation> NewRecommendations() =>
    [
        Recommendation.Create(
            SectionType.Projects, RecommendationPriority.Important,
            RecommendationKind.FewerProjectsThanExpected, "Add more C# projects.", 0.05),
        Recommendation.Create(
            SectionType.Skills, RecommendationPriority.Critical,
            RecommendationKind.MissingMustHaveSkill, "Mention SQL explicitly.", 0.45),
    ];

    private static IReadOnlyList<ReadabilityRecommendation> NewReadabilityRecommendations() =>
    [
        ReadabilityRecommendation.Create(
            ReadabilitySectionType.Contact, RecommendationPriority.Important,
            ReadabilityRecommendationKind.NoPhoneNumber, "Add a phone number.", 0.07),
        ReadabilityRecommendation.Create(
            ReadabilitySectionType.Chronology, RecommendationPriority.Critical,
            ReadabilityRecommendationKind.UnexplainedEmploymentGap,
            "Add an entry covering the 14-month gap before 'Backend Developer'.", 0.16),
    ];

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
