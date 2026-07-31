using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Persistence.Converters;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence.Converters;

public class JsonListConverterTests
{
    private readonly StringListConverter _strings = new();
    private readonly TechnologyListConverter _technologies = new();
    private readonly ProfileListConverter _profiles = new();

    [Fact]
    public void StringList_RoundTripsIncludingUnicodeAndEmbeddedQuotes()
    {
        IReadOnlyList<string> values = ["Lideré la migración", "Said \"no\" to scope creep", string.Empty];

        var json = _strings.ConvertToProvider(values);

        _strings.ConvertFromProvider(json).Should().BeAssignableTo<IReadOnlyList<string>>()
            .Which.Should().Equal(values);
    }

    [Fact]
    public void StringList_EmptyList_RoundTripsAsAnEmptyJsonArray()
    {
        var json = (string)_strings.ConvertToProvider(Array.Empty<string>())!;

        json.Should().Be("[]");
        _strings.ConvertFromProvider(json).Should().BeAssignableTo<IReadOnlyList<string>>()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public void StringList_JsonNull_LoadsAsAnEmptyList()
    {
        _strings.ConvertFromProvider("null").Should().BeAssignableTo<IReadOnlyList<string>>()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public void TechnologyList_RoundTripsThroughTheDomainFactory()
    {
        IReadOnlyList<Technology> values = [Technology.Create("C#"), Technology.Create(".NET")];

        var json = _technologies.ConvertToProvider(values);

        _technologies.ConvertFromProvider(json).Should().BeAssignableTo<IReadOnlyList<Technology>>()
            .Which.Should().Equal(values);
    }

    [Fact]
    public void TechnologyList_IsStoredAsAPlainStringArray()
    {
        var json = (string)_technologies.ConvertToProvider(new[] { Technology.Create("C#") })!;

        json.Should().Be("[\"C#\"]");
    }

    [Fact]
    public void TechnologyList_PersistedValueThatViolatesADomainInvariant_FailsLoudly()
    {
        var act = () => _technologies.ConvertFromProvider("[\"\"]");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ProfileList_RoundTripsEveryOptionalMember()
    {
        IReadOnlyList<Profile> values =
        [
            new("github", "mackroph", Url.Create("https://github.com/mackroph")),
            new("linkedin", null, null)
        ];

        var json = _profiles.ConvertToProvider(values);

        _profiles.ConvertFromProvider(json).Should().BeAssignableTo<IReadOnlyList<Profile>>()
            .Which.Should().Equal(values);
    }

    [Fact]
    public void ProfileList_PersistedUrlThatViolatesADomainInvariant_FailsLoudly()
    {
        var act = () => _profiles.ConvertFromProvider("[{\"Network\":\"github\",\"Username\":null,\"Url\":\"ftp://example.com\"}]");

        act.Should().Throw<InvalidUrlException>();
    }

    [Fact]
    public void ProfileList_EmptyList_RoundTrips()
    {
        var json = _profiles.ConvertToProvider(Array.Empty<Profile>());

        _profiles.ConvertFromProvider(json).Should().BeAssignableTo<IReadOnlyList<Profile>>()
            .Which.Should().BeEmpty();
    }
}
