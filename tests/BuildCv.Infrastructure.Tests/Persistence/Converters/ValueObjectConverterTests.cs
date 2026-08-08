using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Persistence.Converters;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence.Converters;

public class ValueObjectConverterTests
{
    // Shape only: Password.Create parses the algorithm out of the hash, it does not verify anything.
    private const string Argon2Hash = "$argon2id$v=19$m=65536,t=3,p=1$c2FsdHlzYWx0eXNhbHQ=$aGFzaGhhc2hoYXNoaGFzaA==";

    private readonly DateRangeConverter _dateRange = new();
    private readonly PasswordConverter _password = new();

    [Fact]
    public void DateRange_ClosedPeriod_StoresBothEndpoints()
    {
        var period = DateRange.Create(new DateOnly(2020, 1, 15), new DateOnly(2023, 7, 1));

        _dateRange.ConvertToProvider(period).Should().Be("2020-01-15/2023-07-01");
        _dateRange.ConvertFromProvider("2020-01-15/2023-07-01").Should().Be(period);
    }

    [Fact]
    public void DateRange_OpenEndedPeriod_KeepsTheSeparatorAndLeavesTheEndSegmentEmpty()
    {
        var period = DateRange.Create(new DateOnly(2020, 1, 15));

        _dateRange.ConvertToProvider(period).Should().Be("2020-01-15/");

        var loaded = _dateRange.ConvertFromProvider("2020-01-15/").Should().BeOfType<DateRange>().Which;
        loaded.Should().Be(period);
        loaded.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void DateRange_StoredTextNeverExceedsTheDeclaredColumnWidth()
    {
        var text = (string)_dateRange.ConvertToProvider(
            DateRange.Create(new DateOnly(2020, 1, 15), new DateOnly(2023, 7, 1)))!;

        text.Should().HaveLength(DateRangeConverter.MaxLength);
    }

    [Theory]
    [InlineData("2020-01-15")]
    [InlineData("")]
    [InlineData("15/01/2020")]
    [InlineData("2020-01-15/not-a-date")]
    public void DateRange_MalformedPersistedText_FailsLoudly(string text)
    {
        var act = () => _dateRange.ConvertFromProvider(text);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void DateRange_PersistedTextThatViolatesTheDomainInvariant_FailsLoudly()
    {
        var act = () => _dateRange.ConvertFromProvider("2023-07-01/2020-01-15");

        act.Should().Throw<InvalidDateRangeException>();
    }

    [Fact]
    public void Password_RoundTripsThroughTheHash()
    {
        var password = Password.Create(Argon2Hash);

        _password.ConvertToProvider(password).Should().Be(Argon2Hash);

        var loaded = _password.ConvertFromProvider(Argon2Hash).Should().BeOfType<Password>().Which;
        loaded.Should().Be(password);
        loaded.Algorithm.Should().Be("argon2id");
    }

    [Fact]
    public void Password_PersistedHashWithAnUnsupportedAlgorithm_FailsLoudly()
    {
        var act = () => _password.ConvertFromProvider("$md5$v=19$m=1,t=1,p=1$c2FsdA==$aGFzaA==");

        act.Should().Throw<InvalidAccountException>();
    }

    // The claim that makes "no data migration is needed for the sixth section" true, proved rather
    // than argued. This is a LITERAL v1 payload — five members, exactly what is on disk for every
    // analysis scored before Languages existed — and it must still load: Languages deserializes absent
    // as 0.0, the other five still sum to 1.0, the factory accepts it, and the old analysis explains
    // its own arithmetic exactly, with Languages having contributed nothing. Which is what happened.
    [Fact]
    public void ScoringWeights_AVersionOnePayloadStillLoadsAndReadsZeroForLanguages()
    {
        const string v1 =
            """{"Skills":0.45,"Experience":0.2,"Education":0.2,"Certifications":0.1,"Projects":0.05,"SchemaVersion":1}""";

        var weights = ScoringWeightsSnapshotConverter.FromJson(v1);

        v1.Should().NotContain("Languages", "this has to be the shape that is really on disk");

        weights.Languages.Should().Be(0.0, "a v1 payload never carried a sixth weight");
        weights.SchemaVersion.Should().Be(1, "the row must keep saying which model explained it");
        weights.Skills.Should().Be(0.45);
        weights.Education.Should().Be(0.2, "v1 weighted Education at 0.20, and that score stays explained by it");

        // The whole point: a v1 breakdown still produces the total it always produced.
        var breakdown = ScoreBreakdown.Create(1.0, 1.0, 1.0, 1.0, 1.0, 0.0, weights);
        breakdown.WeightedTotal.Should().BeApproximately(1.0, 0.0001);
    }

    // THE INVERSE of the assertion that used to close the test above.
    //
    // While Languages carried no weight, a v1 payload read back as exactly Default() and that equality
    // WAS the behaviour-neutrality claim, measured at the persistence layer. v2 redistributes, so the
    // equality is now false on purpose — and stating that it is false is what stops a future
    // redistribution from being reverted by accident and passing silently.
    //
    // The rollback cliff is named in the sibling test below, which is where it is executed.
    [Fact]
    public void ScoringWeights_AVersionOnePayloadNoLongerMatchesTodaysWeighting()
    {
        const string v1 =
            """{"Skills":0.45,"Experience":0.2,"Education":0.2,"Certifications":0.1,"Projects":0.05,"SchemaVersion":1}""";

        var stored = ScoringWeightsSnapshotConverter.FromJson(v1);
        var today = ScoringWeightsSnapshot.Default();

        // The WEIGHTS, member by member, and NOT `stored.Should().NotBe(today)`. Record equality
        // includes SchemaVersion, so the whole-record comparison is satisfied by the version differing
        // even when every weight is identical — a negative control that reverted the redistribution and
        // left the const at 2 walked straight past it. The claim here is about the numbers.
        stored.Education.Should().NotBe(today.Education,
            "v2 halved Education, so an old row is explained by a different model");
        stored.Languages.Should().NotBe(today.Languages,
            "and funded Languages with what Education lost");

        stored.SchemaVersion.Should().NotBe(ScoringWeightsSnapshot.CurrentSchemaVersion,
            "and the row says so rather than leaving the difference to be inferred from the numbers");

    }

    // THE ROLLBACK CLIFF, executed in both directions rather than described — and narrower on BOTH axes
    // than the obvious phrasing, which is worth stating precisely because "every row is unreadable after
    // v2" is the kind of claim that gets repeated into a deployment plan.
    //
    // WHICH READER: one built before the Languages MEMBER existed, i.e. before PR 1 — not merely before
    // v2. A PR 2 build has the six-member type and reads a v2 payload fine; it just sees weights that
    // differ from its own Default(). The payload below is what a PRE-PR-1 deserializer produces, because
    // it is the only one that drops the member (System.Text.Json defaults JsonUnmappedMemberHandling to
    // Skip) and then re-runs the sum invariant over the five it can see.
    //
    // WHICH ROWS: renormalization decides whether that sum still reaches 1.00.
    //
    //   - Posting stated a language requirement -> Languages carries weight -> the five sum to LESS
    //     than 1.00 and Create throws. The row is UNREADABLE to that build.
    //   - Posting stated none -> Languages is renormalized to 0.0 -> the other five already sum to 1.00
    //     and the row loads, correctly: a section weighted zero contributed nothing to the total, so
    //     the old reader reproduces the same number.
    //
    // So the cliff is real and there is no rolling back past PR 1 once a row of the first kind exists —
    // but the rows it strands are exactly those scored against a posting that asked for a language.
    [Fact]
    public void ScoringWeights_AVersionTwoPayloadIsUnreadableToAnOldReaderOnlyWhenLanguagesCarriesWeight()
    {
        // What an old reader's deserializer produces from a v2 row scored against a posting that DID
        // state a language requirement: the same five members, Languages dropped on the floor.
        const string languagesWeighted =
            """{"Skills":0.45,"Experience":0.2,"Education":0.1,"Certifications":0.1,"Projects":0.05,"SchemaVersion":2}""";

        var act = () => ScoringWeightsSnapshotConverter.FromJson(languagesWeighted);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*0.9*", "the five members it can see sum to 0.90, not 1.00");

        // And the same row shape for a posting that stated NO language requirement, taken from the real
        // renormalization rather than hand-written, so the two halves cannot drift apart.
        var renormalized = ScoringWeightsSnapshot.Default().RenormalizedTo(
            [SectionType.Skills, SectionType.Experience, SectionType.Education,
             SectionType.Certifications, SectionType.Projects]);
        renormalized.Languages.Should().Be(0.0);

        var seenByAnOldReader = ScoringWeightsSnapshot.Create(
            renormalized.Skills, renormalized.Experience, renormalized.Education,
            renormalized.Certifications, renormalized.Projects, 0.0, renormalized.SchemaVersion);

        seenByAnOldReader.Should().Be(renormalized,
            "a section renormalized to zero is invisible to a reader that cannot see it, and harmless");
    }

    [Fact]
    public void ScoringWeights_RoundTripThroughJsonWithSixMembers()
    {
        var weights = ScoringWeightsSnapshot.Default();

        var json = ScoringWeightsSnapshotConverter.ToJson(weights);

        json.Should().Contain("\"Languages\"", "the section now carries weight, and the payload states it");
        ScoringWeightsSnapshotConverter.FromJson(json).Should().Be(weights);
    }

    // FromJson has to carry the version that was WRITTEN, not fall back to the one that ships today.
    //
    // The argument that does this is optional, so dropping it compiles silently, and the two tests
    // that used to catch that now assert 1 against a fallback of 1 — they agree with the bug. The
    // version here is deliberately unequal to CurrentSchemaVersion, and the first assertion fails
    // rather than the test quietly weakening if a future bump ever makes it equal again.
    //
    // Latent until PR 3 moves the const to 2, at which point every historical row would start
    // claiming it was scored under weights that did not exist when it was scored.
    [Fact]
    public void ScoringWeights_ThePersistedSchemaVersionSurvivesTheRoundTrip()
    {
        const int persisted = 7;
        persisted.Should().NotBe(ScoringWeightsSnapshot.CurrentSchemaVersion,
            "a version equal to the fallback cannot detect the fallback");

        const string stored =
            """{"Skills":0.45,"Experience":0.2,"Education":0.2,"Certifications":0.1,"Projects":0.05,"Languages":0.0,"SchemaVersion":7}""";

        // Parsed from a literal rather than round-tripped through ToJson, so a writer that dropped the
        // member could not make this pass by never emitting it.
        ScoringWeightsSnapshotConverter.FromJson(stored).SchemaVersion.Should().Be(persisted);

        var written = ScoringWeightsSnapshot.Create(0.45, 0.20, 0.20, 0.10, 0.05, 0.00, persisted);
        ScoringWeightsSnapshotConverter.FromJson(ScoringWeightsSnapshotConverter.ToJson(written))
            .Should().Be(written);
    }

    // The column-width guard, on a payload that can actually fail it.
    //
    // Default() serializes to roughly 120 characters against a 256 cap, so measuring THAT would pass
    // against almost any regression — including one that added two more members. These weights come
    // out of division rather than literals, so every one of them serializes at full round-trip
    // precision, which is the widest a weights payload can legitimately get.
    [Fact]
    public void ScoringWeights_AFullPrecisionPayloadStillFitsTheDeclaredColumnWidth()
    {
        var skills = 1.0 / 3.0;
        var experience = 1.0 / 7.0;
        var education = 1.0 / 9.0;
        var certifications = 1.0 / 11.0;
        var projects = 1.0 / 13.0;
        var languages = 1.0 - (skills + experience + education + certifications + projects);

        var weights = ScoringWeightsSnapshot.Create(
            skills, experience, education, certifications, projects, languages);

        var json = ScoringWeightsSnapshotConverter.ToJson(weights);

        json.Length.Should().BeGreaterThan(180,
            "a payload that does not approach the cap cannot be evidence the cap is wide enough");
        json.Length.Should().BeLessThanOrEqualTo(ScoringWeightsSnapshotConverter.MaxLength,
            "a payload wider than the column truncates, and a truncated snapshot cannot be parsed back");

        // Truncation is not the only failure: a value that lost precision on the way out would come
        // back as a different snapshot, and the 1.0-sum check would not necessarily catch it.
        ScoringWeightsSnapshotConverter.FromJson(json).Should().Be(weights);
    }

    // Reading goes back through the factory, so a persisted set that no longer sums to 1.0 has to fail
    // loudly instead of silently explaining a score with weights that could not have produced it.
    [Fact]
    public void ScoringWeights_APayloadThatNoLongerSumsToOne_FailsLoudly()
    {
        const string broken =
            """{"Skills":0.45,"Experience":0.2,"Education":0.2,"Certifications":0.1,"Projects":0.05,"Languages":0.1,"SchemaVersion":2}""";

        var act = () => ScoringWeightsSnapshotConverter.FromJson(broken);

        act.Should().Throw<ArgumentException>();
    }

    // The readability weighting, through the same JSON column mechanism and with the same three
    // guarantees: the shape survives, the PERSISTED version survives, and a set that no longer sums to
    // 1.0 fails loudly rather than explaining a report it could not have produced.
    [Fact]
    public void ReadabilityWeights_RoundTripThroughJsonWithFiveMembers()
    {
        var weights = ReadabilityWeightsSnapshot.Default();

        var json = ReadabilityWeightsSnapshotConverter.ToJson(weights);

        json.Should().Contain("\"AtsParseability\"",
            "the section is shaped and stored even while it is renormalized out of every report");
        ReadabilityWeightsSnapshotConverter.FromJson(json).Should().Be(weights);
    }

    // The version has to be the one that was WRITTEN, not the one that ships today. The argument doing
    // that is optional, so dropping it compiles silently and every historical row would start claiming
    // it was produced by whatever model is current. The version here is deliberately unequal to
    // CurrentSchemaVersion, so it cannot agree with the fallback.
    [Fact]
    public void ReadabilityWeights_ThePersistedSchemaVersionSurvivesTheRoundTrip()
    {
        const int persisted = 7;
        persisted.Should().NotBe(ReadabilityWeightsSnapshot.CurrentSchemaVersion,
            "a version equal to the fallback cannot detect the fallback");

        const string stored =
            """{"Completeness":0.3,"Contact":0.2,"Achievements":0.25,"Chronology":0.15,"AtsParseability":0.1,"SchemaVersion":7}""";

        // Parsed from a literal rather than round-tripped through ToJson, so a writer that dropped the
        // member could not make this pass by never emitting it.
        ReadabilityWeightsSnapshotConverter.FromJson(stored).SchemaVersion.Should().Be(persisted);
    }

    // THE PAYLOAD EVERY STORED REPORT ACTUALLY HOLDS, and the one that can fail the width guard: the
    // renormalized set comes out of a division, so all five members serialize at full round-trip
    // precision. Default() serializes to about a third of the cap and would pass against almost any
    // regression.
    [Fact]
    public void ReadabilityWeights_ARenormalizedPayloadStillFitsTheDeclaredColumnWidth()
    {
        var weights = ReadabilityWeightsSnapshot.Default().RenormalizedTo(
        [
            ReadabilitySectionType.Completeness,
            ReadabilitySectionType.Contact,
            ReadabilitySectionType.Achievements,
            ReadabilitySectionType.Chronology,
        ]);

        var json = ReadabilityWeightsSnapshotConverter.ToJson(weights);

        json.Length.Should().BeGreaterThan(100,
            "a payload that does not approach the cap cannot be evidence the cap is wide enough");
        json.Length.Should().BeLessThanOrEqualTo(ReadabilityWeightsSnapshotConverter.MaxLength,
            "a payload wider than the column truncates, and a truncated snapshot cannot be parsed back");

        // Truncation is not the only failure: a value that lost precision on the way out would come back
        // as a different snapshot, and the 1.0-sum check would not necessarily catch it.
        ReadabilityWeightsSnapshotConverter.FromJson(json).Should().Be(weights);
    }

    [Fact]
    public void ReadabilityWeights_APayloadThatNoLongerSumsToOne_FailsLoudly()
    {
        const string broken =
            """{"Completeness":0.3,"Contact":0.2,"Achievements":0.25,"Chronology":0.15,"AtsParseability":0.2,"SchemaVersion":1}""";

        var act = () => ReadabilityWeightsSnapshotConverter.FromJson(broken);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StronglyTypedIds_RoundTripThroughGuid()
    {
        var value = Guid.NewGuid();

        new AccountIdConverter().ConvertFromProvider(value).Should().Be(new AccountId(value));
        new ResumeIdConverter().ConvertFromProvider(value).Should().Be(new ResumeId(value));
        new JobPostingIdConverter().ConvertFromProvider(value).Should().Be(new JobPostingId(value));
        new OrganizationIdConverter().ConvertFromProvider(value).Should().Be(new OrganizationId(value));
        new AnalysisIdConverter().ConvertFromProvider(value).Should().Be(new AnalysisId(value));
        new ReadabilityReportIdConverter().ConvertFromProvider(value)
            .Should().Be(new ReadabilityReportId(value));
    }

    [Fact]
    public void StronglyTypedIds_UnwrapToTheirGuid()
    {
        var value = Guid.NewGuid();

        new AccountIdConverter().ConvertToProvider(new AccountId(value)).Should().Be(value);
        new AnalysisIdConverter().ConvertToProvider(new AnalysisId(value)).Should().Be(value);
    }

    [Fact]
    public void StronglyTypedIds_EmptyPersistedGuid_FailsLoudly()
    {
        var act = () => new ResumeIdConverter().ConvertFromProvider(Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ForValueObject_ComparesModelValuesNotProviderBytes()
    {
        var comparer = ConvertedComparers.ForValueObject<Email>();
        var left = Email.Create("candidate@example.com");
        var right = Email.Create("CANDIDATE@example.com");

        comparer.Equals(left, right).Should().BeTrue();
        comparer.GetHashCode(left).Should().Be(comparer.GetHashCode(right));
        comparer.Equals(left, Email.Create("recruiter@example.com")).Should().BeFalse();
        comparer.Snapshot(left).Should().Be(left);
    }

    [Fact]
    public void ForList_IsOrderSensitiveAndSnapshotsIndependently()
    {
        var comparer = ConvertedComparers.ForList<string>();
        var source = new List<string> { "a", "b" };

        comparer.Equals(source, ["a", "b"]).Should().BeTrue();
        comparer.Equals(source, ["b", "a"]).Should().BeFalse();
        comparer.Equals(source, ["a"]).Should().BeFalse();

        var snapshot = comparer.Snapshot(source);
        source.Add("c");
        snapshot.Should().Equal("a", "b");
    }

    [Fact]
    public void ForList_EqualListsShareAHashCode()
    {
        var comparer = ConvertedComparers.ForList<Technology>();
        IReadOnlyList<Technology> left = [Technology.Create("C#"), Technology.Create(".NET")];
        IReadOnlyList<Technology> right = [Technology.Create("C#"), Technology.Create(".NET")];

        comparer.GetHashCode(left).Should().Be(comparer.GetHashCode(right));
    }
}
