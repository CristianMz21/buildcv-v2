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
}
