using BuildCv.Application.Identity;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Application.Tests.Identity;

public class RegisterAccountHandlerTests
{
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly RegisterAccountHandler _handler;

    public RegisterAccountHandlerTests() =>
        _handler = new RegisterAccountHandler(_accounts, _hasher);

    [Fact]
    public async Task Register_success_returns_account_dto_and_persists_account()
    {
        var result = await _handler.Handle(new RegisterAccountCommand("new@example.com", "super-secret-password"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Email.Should().Be("new@example.com");
        result.Value.Role.Should().Be(nameof(Role.Candidate));
        result.Value.Status.Should().Be(nameof(AccountStatus.Active));
        result.Value.IsEmailVerified.Should().BeFalse();

        var persisted = await _accounts.GetByEmailAsync(Email.Create("new@example.com"));
        persisted.Should().NotBeNull();
        persisted!.Password.Hash.Should().NotBe("super-secret-password");
        persisted.Password.Hash.Should().StartWith("$argon2id$");
    }

    [Fact]
    public async Task Register_duplicate_email_fails()
    {
        await _handler.Handle(new RegisterAccountCommand("dup@example.com", "password-one"));

        var result = await _handler.Handle(new RegisterAccountCommand("dup@example.com", "password-two"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Email is already registered.");
    }

    [Fact]
    public async Task Register_invalid_email_fails()
    {
        var result = await _handler.Handle(new RegisterAccountCommand("not-an-email", "some-password"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(Role.Candidate)]
    [InlineData(Role.Recruiter)]
    public async Task Register_self_assignable_role_succeeds(Role role)
    {
        var result = await _handler.Handle(
            new RegisterAccountCommand($"{role}@example.com", "super-secret-password", role));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Role.Should().Be(role.ToString());
    }

    [Fact]
    public async Task Register_admin_role_fails_and_persists_nothing()
    {
        var result = await _handler.Handle(
            new RegisterAccountCommand("escalate@example.com", "super-secret-password", Role.Admin));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Role is not available for self-registration.");
        (await _accounts.GetByEmailAsync(Email.Create("escalate@example.com"))).Should().BeNull();
    }

    [Fact]
    public async Task Register_undefined_role_value_fails()
    {
        // Enum.TryParse at the edge happily produces out-of-range values from numeric input,
        // so the handler must reject anything outside the self-assignable allowlist.
        var result = await _handler.Handle(
            new RegisterAccountCommand("weird@example.com", "super-secret-password", (Role)99));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Role is not available for self-registration.");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("hunter2")]
    [InlineData("11character")]
    public async Task Register_weak_password_fails_and_persists_nothing(string weak)
    {
        var result = await _handler.Handle(new RegisterAccountCommand("weak@example.com", weak));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be($"Password must be at least {PasswordPolicy.MinLength} characters.");
        (await _accounts.GetByEmailAsync(Email.Create("weak@example.com"))).Should().BeNull();
    }

    [Fact]
    public async Task Register_weak_password_error_never_echoes_the_password()
    {
        const string weak = "hunter2";

        var result = await _handler.Handle(new RegisterAccountCommand("weak@example.com", weak));

        // Assert the failure FIRST. Without it, a build that stopped rejecting weak passwords
        // returns a null Error, NotContain passes vacuously, and this test reports green while
        // proving nothing — it would survive the very regression it exists to catch.
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty().And.NotContain(weak);
    }

    [Fact]
    public async Task Register_rejects_the_weak_password_before_hashing_it()
    {
        // Argon2id is deliberately expensive. A password the policy will refuse must not be
        // allowed to buy that work — otherwise the cheapest request on the API is the one that
        // fails. Counting hasher calls is what makes this assertion falsifiable: validating
        // after the hash would still return the same error and still persist nothing.
        var before = _hasher.HashCount;

        await _handler.Handle(new RegisterAccountCommand("weak@example.com", "a"));

        _hasher.HashCount.Should().Be(before);
    }
}
