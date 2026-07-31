using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Common.ValueObjects;

public class EmailTests
{
    [Fact]
    public void Email_with_valid_format_can_be_created()
    {
        var email = Email.Create("cristian@example.com");

        email.Value.Should().Be("cristian@example.com");
    }

    [Fact]
    public void Email_is_normalized_to_lowercase()
    {
        var email = Email.Create("Cristian@Example.COM");

        email.Value.Should().Be("cristian@example.com");
    }

    [Fact]
    public void Email_with_subdomain_can_be_created()
    {
        var email = Email.Create("user@mail.example.com");

        email.Value.Should().Be("user@mail.example.com");
    }

    [Fact]
    public void Email_with_dots_and_special_chars_can_be_created()
    {
        var email = Email.Create("user.name+tag@example.com");

        email.Value.Should().Be("user.name+tag@example.com");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Email_with_empty_value_throws_exception(string value)
    {
        var act = () => Email.Create(value);

        act.Should().Throw<Exception>();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("invalid@")]
    [InlineData("@invalid")]
    [InlineData("invalid.com")]
    [InlineData("invalid@.com")]
    [InlineData("invalid@com.")]
    public void Email_with_invalid_format_throws_invalid_email(string value)
    {
        var act = () => Email.Create(value);

        act.Should().Throw<InvalidEmailException>();
    }

    [Fact]
    public void Email_exceeding_max_length_throws_invalid_email()
    {
        var longEmail = new string('a', 250) + "@example.com";

        var act = () => Email.Create(longEmail);

        act.Should().Throw<InvalidEmailException>();
    }

    [Fact]
    public void Email_equality_works()
    {
        var email1 = Email.Create("test@example.com");
        var email2 = Email.Create("TEST@EXAMPLE.COM");

        email1.Should().Be(email2);
    }

    [Fact]
    public void Email_implicit_conversion_to_string()
    {
        var email = Email.Create("test@example.com");

        string value = email;

        value.Should().Be("test@example.com");
    }

    [Fact]
    public void TryCreate_with_valid_email_returns_true()
    {
        var result = Email.TryCreate("test@example.com", out var email);

        result.Should().BeTrue();
        email.Should().NotBeNull();
    }

    [Fact]
    public void TryCreate_with_invalid_email_returns_false()
    {
        var result = Email.TryCreate("invalid", out var email);

        result.Should().BeFalse();
        email.Should().BeNull();
    }
}
