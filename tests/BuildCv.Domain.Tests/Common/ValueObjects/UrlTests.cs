using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Common.ValueObjects;

public class UrlTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path?query=value")]
    [InlineData("https://sub.example.com/path")]
    public void Url_with_valid_format_can_be_created(string value)
    {
        var url = Url.Create(value);

        url.Value.Should().Be(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Url_with_empty_value_throws_exception(string value)
    {
        var act = () => Url.Create(value);

        act.Should().Throw<Exception>();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("ftp://example.com")]
    [InlineData("example.com")]
    [InlineData("//example.com")]
    public void Url_with_invalid_format_throws_invalid_url(string value)
    {
        var act = () => Url.Create(value);

        act.Should().Throw<InvalidUrlException>();
    }

    [Fact]
    public void Url_equality_works()
    {
        var url1 = Url.Create("https://example.com");
        var url2 = Url.Create("https://example.com");

        url1.Should().Be(url2);
    }

    [Fact]
    public void Url_implicit_conversion_to_string()
    {
        var url = Url.Create("https://example.com");

        string value = url;

        value.Should().Be("https://example.com");
    }

    [Fact]
    public void TryCreate_with_valid_url_returns_true()
    {
        var result = Url.TryCreate("https://example.com", out var url);

        result.Should().BeTrue();
        url.Should().NotBeNull();
    }

    [Fact]
    public void TryCreate_with_invalid_url_returns_false()
    {
        var result = Url.TryCreate("invalid", out var url);

        result.Should().BeFalse();
        url.Should().BeNull();
    }
}
