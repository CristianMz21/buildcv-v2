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
}
