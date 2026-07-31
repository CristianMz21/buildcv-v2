using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
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

    [Fact]
    public void StronglyTypedIds_RoundTripThroughGuid()
    {
        var value = Guid.NewGuid();

        new AccountIdConverter().ConvertFromProvider(value).Should().Be(new AccountId(value));
        new ResumeIdConverter().ConvertFromProvider(value).Should().Be(new ResumeId(value));
        new JobPostingIdConverter().ConvertFromProvider(value).Should().Be(new JobPostingId(value));
        new OrganizationIdConverter().ConvertFromProvider(value).Should().Be(new OrganizationId(value));
        new AnalysisIdConverter().ConvertFromProvider(value).Should().Be(new AnalysisId(value));
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
        var comparer = EncryptedComparers.ForValueObject<Email>();
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
        var comparer = EncryptedComparers.ForList<string>();
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
        var comparer = EncryptedComparers.ForList<Technology>();
        IReadOnlyList<Technology> left = [Technology.Create("C#"), Technology.Create(".NET")];
        IReadOnlyList<Technology> right = [Technology.Create("C#"), Technology.Create(".NET")];

        comparer.GetHashCode(left).Should().Be(comparer.GetHashCode(right));
    }
}
