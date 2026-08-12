using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Security;
using BuildCv.Infrastructure.Tests.Security;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// THE HALF OF THE LEAK QUESTION THE API-TIER TEST STRUCTURALLY CANNOT ASK.
//
// ObservabilityLeakTests drives every candidate-content path this API exposes over HTTP and asserts
// nothing reaches a log, a scope, a metric tag or an Activity attribute. It works, and a planted leak
// reds it. But it runs on ApiTestFactory, which forces the IN-MEMORY persistence provider — so the
// entire EF Core logging surface is outside its reach:
//
//   * the command text and the parameter list, written on every read and every write;
//   * the exception chain behind a failed SaveChanges, which on SQL Server can quote the offending
//     value back — that is exactly how error 2628 was found carrying candidate text into the log
//     during the CV-import phase, and why ValueTooLongException deliberately drops its inner
//     exception;
//   * connection, transaction and query-compilation diagnostics.
//
// So the paths most likely to leak are the ones the existing test cannot observe. This closes that,
// against a real SQL Server, on the same context shape the repositories run on in production.
//
// WHAT MAKES THIS MORE THAN A GREEN LIGHT — an absence assertion is the easiest kind of test to write
// so that it cannot fail, so four things are asserted alongside it:
//
//   1. The sentinels reach PLAINTEXT columns as well as encrypted ones. Skills.Name, Languages.Name
//      and Projects.Technologies are analytical columns and are stored as the candidate typed them;
//      they are what a parameter log would expose. Sentinels planted only in varbinary columns would
//      make this test unfailable, because a parameter log of an envelope is hex.
//   2. The INSERT that carried those columns is asserted to have been LOGGED (AnInsertOfThePlaintext
//      ColumnsWasLogged below), so the absence assertion is known to be searching the record a leak
//      would sit in rather than an empty recorder.
//   3. A failing SaveChanges is driven, and an Error-level record carrying an exception chain is
//      asserted, so the vendor-error surface is proven observed rather than assumed absent.
//   4. TheTestHostNeverEnablesSensitiveDataLogging reads the flag back off the built options. Turning
//      it on is the negative control for this test AND is a one-line change that would silently make
//      every parameterised query a PII log; the assertion is what stops the control being left behind.
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EfCoreObservabilityLeakTests
{
    // Alphabetic and distinctive, so a failure names the field rather than "something leaked", and so
    // they survive the Domain's own value-object rules.
    //
    // PLAINTEXT columns — ResumeConfiguration classifies these as analytical, so SQL Server stores the
    // characters below verbatim and a parameter log would print them.
    private const string SkillName = "Zzqskillname";
    private const string LanguageName = "Zzqlanguagename";
    private const string TechnologyName = "Zzqtechnology";

    // ENCRYPTED columns — sealed at rest, so a parameter log prints an envelope. Included anyway
    // because encryption covers the column and covers nothing else: a value can still reach a log by
    // travelling through an exception message, a scope or a compiled query expression.
    private const string FullName = "Zzqfullname";
    private const string Location = "Zzqlocation";
    private const string Summary = "Zzqsummary";
    private const string Organization = "Zzqorganization";
    private const string Position = "Zzqposition";
    private const string Highlight = "Zzqhighlight";
    private const string Institution = "Zzqinstitution";
    private const string Certificate = "Zzqcertificate";
    private const string Interest = "Zzqinterest";
    private const string Fluency = "Zzqfluency";
    private const string EmailLocalPart = "zzqemail";

    private static readonly string[] Sentinels =
    [
        SkillName, LanguageName, TechnologyName, FullName, Location, Summary, Organization, Position,
        Highlight, Institution, Certificate, Interest, Fluency, EmailLocalPart
    ];

    private readonly SqlServerFixture _fixture;

    public EfCoreObservabilityLeakTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task NoCandidateContentReachesTheEfCoreLog()
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = RecordingLoggerFactory(recorder);

        var owner = AccountId.New();
        var storedId = await ImportASentinelResumeAsync(loggerFactory, owner);
        await ReadTheResumeBackAsync(loggerFactory, owner, storedId);
        await UpdateTheResumeAsync(loggerFactory, storedId);
        await DeleteTheResumeAsync(loggerFactory, storedId);
        await DriveAFailingSaveAsync(loggerFactory);

        var captured = recorder.AllText;

        // One scope, so a run that leaks a plaintext column AND an encrypted one reports both. Without
        // it the first failure throws and the second is never looked at — which is how a fix aimed at
        // the reported half gets called done.
        using (new AssertionScope())
        {
            AssertNoSentinelIn(captured);
            AnInsertOfThePlaintextColumnsWasLogged(captured);
            TheEfCoreLoggingSurfaceWasObserved(recorder);
        }
    }

    // The negative control for the test above, stated as an assertion so it cannot be left flipped on.
    //
    // EnableSensitiveDataLogging(true) makes EF Core write parameter VALUES into "Executed DbCommand",
    // which puts Skills.Name, Languages.Name and Projects.Technologies — candidate text, in the clear,
    // by design — into the log the test above searches. That is what proves the test can fail. It is
    // also, on a production context, a one-line change that would turn every parameterised query into a
    // PII log; AddInfrastructure is pinned by PersistenceRegistrationTests and this pins the test host,
    // which is the context this file measures.
    [Fact]
    public void TheTestHostNeverEnablesSensitiveDataLogging()
    {
        using var context = _fixture.NewApplicationContext();

        var core = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>();

        core.Should().NotBeNull("every context carries the core options extension");
        core!.IsSensitiveDataLoggingEnabled
            .Should().BeFalse(
                "flipping this to true is the negative control for NoCandidateContentReachesTheEfCoreLog, "
                + "and a control left behind would make every parameterised query a PII log");
    }

    // Trace, and no filter rules, so nothing EF Core writes is dropped before the recorder sees it. An
    // absence assertion over a log filtered to Warning would certify the levels nobody was worried
    // about — and EF Core writes its command text at Information and its query compilation at Debug.
    private static ILoggerFactory RecordingLoggerFactory(RecordingLoggerProvider recorder) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(recorder);
        });

    private async Task<ResumeId> ImportASentinelResumeAsync(ILoggerFactory loggerFactory, AccountId owner)
    {
        await using var writer = _fixture.NewRecordedContext(loggerFactory);
        var handler = new CreateResumeFromDraftHandler(
            TestRepositories.Resumes(writer), TestRepositories.CandidateProfiles(writer), TestImportEvidence.Protector());
        var result = await handler.Handle(new CreateResumeFromDraftCommand(owner, SentinelDraft()));

        result.FieldErrors.Should().BeEmpty();
        result.IsSuccess.Should().BeTrue();
        return result.Resume!.Id;
    }

    // Both read shapes: the split-query load of one aggregate and the keyset page. They compile
    // different queries and log different command text.
    private async Task ReadTheResumeBackAsync(
        ILoggerFactory loggerFactory, AccountId owner, ResumeId storedId)
    {
        await using var reader = _fixture.NewRecordedContext(loggerFactory);
        var repository = TestRepositories.Resumes(reader);

        var reloaded = await repository.GetByIdAsync(storedId);

        // The sentinels have to have made the round trip, or this test drove a resume that never held
        // candidate content and its absence assertion measured nothing.
        reloaded.Should().NotBeNull();
        reloaded!.Skills.Should().ContainSingle().Which.Name.Name.Should().Be(SkillName);
        reloaded.Languages.Should().ContainSingle().Which.Name.Should().Be(LanguageName);
        reloaded.ContactInformation.FullName.Value.Should().Be(FullName);

        var page = await repository.GetPageByOwnerIdAsync(owner, PageRequests.Of(limit: 5));
        page.Items.Should().ContainSingle();
    }

    // The UPDATE path. A second skill on a tracked aggregate, so the write is an INSERT into an owned
    // collection plus an UPDATE of the root's rowversion — a different command text again.
    private async Task UpdateTheResumeAsync(ILoggerFactory loggerFactory, ResumeId storedId)
    {
        await using var context = _fixture.NewRecordedContext(loggerFactory);
        var repository = TestRepositories.Resumes(context);

        var resume = await repository.GetByIdAsync(storedId);
        resume!.AddSkill(Skill.Create(Technology.Create($"{SkillName}two"), SkillLevel.Advanced, 3));

        await repository.UpdateAsync(resume);
    }

    // The soft-delete tombstone and its cascade to the analyses and readability reports keyed by this
    // resume, which is three more statements the audit interceptor rewrites on the way out.
    private async Task DeleteTheResumeAsync(ILoggerFactory loggerFactory, ResumeId storedId)
    {
        await using var context = _fixture.NewRecordedContext(loggerFactory);
        await TestRepositories.Resumes(context).DeleteAsync(storedId);
    }

    // The vendor-error surface, driven by the one duplicate this schema can actually produce: two
    // registrations of the same address, refused by the filtered unique index on EmailHash. EF Core
    // logs the failure at Error with the whole SqlException chain attached — the surface that carried
    // candidate text in the 2628 incident — and SQL Server's own duplicate-key message quotes the
    // OFFENDING KEY, which here is the blind-index digest rather than the address. The absence
    // assertion over `zzqemail` is what states that difference as a property instead of a hope.
    private async Task DriveAFailingSaveAsync(ILoggerFactory loggerFactory)
    {
        var address = $"{EmailLocalPart}.{Guid.NewGuid():N}@example.com";

        await using (var first = _fixture.NewRecordedContext(loggerFactory))
            await TestRepositories.Accounts(first).AddAsync(NewAccount(address));

        await using var second = _fixture.NewRecordedContext(loggerFactory);
        var act = async () => await TestRepositories.Accounts(second).AddAsync(NewAccount(address));

        await act.Should().ThrowAsync<DuplicateKeyException>(
            "a failing SaveChanges is the point of this drive — without one, no exception chain is "
            + "ever logged and the Error-level assertion below would be measuring nothing");
    }

    private static Account NewAccount(string email) =>
        Account.Create(Email.Create(email), Password.Create(new PasswordHasher().Hash("correct-horse-battery")));

    private static void AssertNoSentinelIn(IReadOnlyList<string> captured)
    {
        var leaks = captured
            .SelectMany(text => Sentinels
                .Where(sentinel => text.Contains(sentinel, StringComparison.OrdinalIgnoreCase))
                .Select(sentinel => $"'{sentinel}' in: {Truncate(text)}"))
            .Distinct()
            .ToList();

        leaks.Should().BeEmpty(
            "no part of a CV may reach EF Core's log — {0} occurrence(s): {1}",
            leaks.Count, string.Join(" | ", leaks));
    }

    // Proof that the recorder is looking at the exact record a leak would sit in. The INSERT below is
    // the statement that carried Skills.Name, Languages.Name and Projects.Technologies, and it is the
    // one whose parameter list turns into candidate text the moment sensitive-data logging is on. If
    // this assertion is red the absence assertion above certifies nothing.
    private static void AnInsertOfThePlaintextColumnsWasLogged(IReadOnlyList<string> captured)
    {
        captured.Should().Contain(
            text => text.Contains("INSERT INTO [resumes].[Skills]", StringComparison.Ordinal),
            "the write that carried the plaintext sentinel columns must appear in the captured log, "
            + "or the absence assertion is searching a recorder that saw nothing");
        captured.Should().Contain(
            text => text.Contains("INSERT INTO [resumes].[Languages]", StringComparison.Ordinal));
    }

    // The breadth of what was captured, asserted per surface rather than as one "not empty". Each of
    // these is a distinct EF Core logger category, and a recorder wired up so that it only ever saw one
    // of them would still pass a bare NotBeEmpty.
    private static void TheEfCoreLoggingSurfaceWasObserved(RecordingLoggerProvider recorder)
    {
        var categories = recorder.Records.Select(record => record.Category).Distinct().ToList();

        categories.Should().Contain("Microsoft.EntityFrameworkCore.Database.Command",
            "command text and the parameter list are the surface the in-memory provider does not have");
        categories.Should().Contain("Microsoft.EntityFrameworkCore.Query");
        categories.Should().Contain("Microsoft.EntityFrameworkCore.Update");

        recorder.Records.Should().Contain(
            record => record.Level == LogLevel.Error && record.Exception != null,
            "the failed SaveChanges must have been logged WITH its exception chain — that chain is "
            + "where SQL Server quotes the offending value back, and it is the surface error 2628 leaked "
            + "through");
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";

    private static ResumeDraft SentinelDraft() => new(
        Contact: new ContactDraft(
            FullName: FullName,
            Email: $"{EmailLocalPart}.{Guid.NewGuid():N}@example.com",
            PhoneNumber: "+541155550123",
            Location: Location,
            Website: "https://zzq.example.com",
            Summary: Summary,
            Profiles: [new ProfileDraft("GitHub", "zzqhandle", "https://github.com/zzqhandle")]),
        Experiences:
        [
            new ExperienceDraft("Professional", Organization, Position,
                "2019-03-01", "2023-06-30", Summary, [Highlight])
        ],
        Educations:
        [
            new EducationDraft(Institution, "Zzqdegree", "Zzqfield",
                "2012-03-01", "2017-12-01", "Zzqgrade", "Bachelor")
        ],
        Skills: [new SkillDraft(SkillName, "Advanced", "7")],
        Projects:
        [
            new ProjectDraft("Zzqproject", "2024-01-01", null, "Zzqdescription",
                "https://github.com/zzqhandle/zzqproject", "https://zzqproject.example.com",
                [TechnologyName], [Highlight])
        ],
        Certificates:
        [
            new CertificateDraft(Certificate, "Zzqissuer", "zzqcred",
                "https://zzq.example.com/zzqcred", "2024-01-01", "2027-01-01")
        ],
        Languages: [new LanguageDraft(LanguageName, Fluency, "Native")],
        Awards: [new AwardDraft("Zzqaward", "Zzqawarder", "2021-11-05", Summary)],
        Publications:
        [
            new PublicationDraft("Zzqpublication", "Zzqpublisher", "https://zzq.example.com/paper",
                "2022-05-01", Summary)
        ],
        Interests: [new InterestDraft(Interest, ["zzqkeyword"])],
        References:
        [
            new ReferenceDraft("Zzqreferee", Position, Organization,
                $"zzqreferee.{Guid.NewGuid():N}@example.com", "+541155550999", Summary)
        ]);
}
