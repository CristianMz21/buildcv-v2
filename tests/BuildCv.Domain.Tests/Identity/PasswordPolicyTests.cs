using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Identity;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("short")]
    [InlineData("elevenchar")]
    [InlineData("11character")]
    public void Validate_TooShort_Throws(string password)
    {
        var act = () => PasswordPolicy.Validate(password);

        act.Should().Throw<WeakPasswordException>()
            .WithMessage($"Password must be at least {PasswordPolicy.MinLength} characters.");
    }

    [Fact]
    public void Validate_AtTheMinimum_Succeeds()
    {
        var atMinimum = new string('x', PasswordPolicy.MinLength);

        var act = () => PasswordPolicy.Validate(atMinimum);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_TooLong_Throws()
    {
        var tooLong = new string('x', PasswordPolicy.MaxLength + 1);

        var act = () => PasswordPolicy.Validate(tooLong);

        act.Should().Throw<WeakPasswordException>()
            .WithMessage($"Password must be at most {PasswordPolicy.MaxLength} characters.");
    }

    [Fact]
    public void Validate_AtTheMaximum_Succeeds()
    {
        var atMaximum = new string('x', PasswordPolicy.MaxLength);

        var act = () => PasswordPolicy.Validate(atMaximum);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("               ")]
    [InlineData("\t\t\t\t\t\t\t\t\t\t\t\t")]
    public void Validate_BlankOrWhitespaceOnly_Throws(string? password)
    {
        var act = () => PasswordPolicy.Validate(password);

        act.Should().Throw<WeakPasswordException>();
    }

    // NIST SP 800-63B advises AGAINST composition rules: they push people to "Password1!" and
    // buy less than the length they cost. A long passphrase of nothing but lowercase letters
    // and spaces is a good password and must be accepted.
    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("todos los caballos del rey")]
    [InlineData("aaaaaaaaaaaaaaaaaaaa")]
    public void Validate_LongButWithoutMixedCharacterClasses_Succeeds(string password)
    {
        var act = () => PasswordPolicy.Validate(password);

        act.Should().NotThrow();
    }

    // The message reaches a 400 body and the application log. It names the limit, never the
    // value — the same rule InvalidResumeNameException follows, and for a stronger reason:
    // this value is a credential.
    [Fact]
    public void Validate_Rejection_NeverQuotesTheValue()
    {
        const string secret = "hunter2";

        var act = () => PasswordPolicy.Validate(secret);

        act.Should().Throw<WeakPasswordException>()
            .Which.Message.Should().NotContain(secret);
    }

    [Fact]
    public void WeakPasswordException_IsADomainException()
    {
        // The Application handlers catch DomainException and turn it into a Result failure,
        // which the Api turns into a 400. Breaking this inheritance would turn every weak
        // password into an unhandled 500.
        typeof(WeakPasswordException).Should().BeAssignableTo<DomainException>();
    }
}
