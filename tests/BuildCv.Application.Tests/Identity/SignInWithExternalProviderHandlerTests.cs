using BuildCv.Application.Identity;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Application.Tests.Identity;

public class SignInWithExternalProviderHandlerTests
{
    private const string Token = "a-token";
    private const string Subject = "google-subject-1";
    private const string Address = "candidate@example.com";

    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeRefreshTokenRepository _refreshTokens = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly FakeTokenService _tokenService = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeExternalIdentityVerifier _google = new();

    private SignInWithExternalProviderHandler Handler() =>
        new([_google], _accounts, _refreshTokens, _tokenService, _time);

    private Task<Result<AuthResult>> SignInAsync(string provider = "google", string token = Token) =>
        Handler().Handle(new SignInWithExternalProviderCommand(provider, token));

    private async Task<Account> SeedPasswordAccountAsync(string email = Address)
    {
        var account = Account.Create(Email.Create(email), Password.Create(_hasher.Hash("a-password")));
        await _accounts.AddAsync(account);
        return account;
    }

    [Fact]
    public async Task A_new_address_creates_an_account_that_has_no_password_and_a_verified_email()
    {
        _google.Accepting(Token, Subject, Address);

        var result = await SignInAsync();

        result.IsSuccess.Should().BeTrue();

        var account = await _accounts.GetByEmailAsync(Email.Create(Address));
        account.Should().NotBeNull();
        account!.HasPassword.Should().BeFalse();
        account.IsEmailVerified.Should().BeTrue();
        account.ExternalSubject.Should().Be(Subject);
    }

    // AUTO-LINKING, which is the behaviour our user chose: the same address does not become a second
    // account. Asserted on the ID rather than on a count, because a second account with the same
    // address would also leave the first one findable.
    [Fact]
    public async Task An_address_that_already_has_a_password_account_signs_into_that_account()
    {
        var existing = await SeedPasswordAccountAsync();
        _google.Accepting(Token, Subject, Address);

        var result = await SignInAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccountId.Should().Be(existing.Id);

        var account = await _accounts.GetByIdAsync(existing.Id);
        account!.HasPassword.Should().BeTrue("linking a provider must not remove the password");
        account.ExternalSubject.Should().Be(Subject);
    }

    // THE REASSIGNED ADDRESS. A Workspace domain deletes alice@corp.com and recreates it for the next
    // Alice, who arrives with the same address and a new subject. Linking on the address alone would
    // hand her the previous Alice's CVs.
    [Fact]
    public async Task An_address_relinked_under_a_different_subject_is_refused()
    {
        _google.Accepting(Token, Subject, Address);
        (await SignInAsync()).IsSuccess.Should().BeTrue();

        _google.Accepting("second-token", "google-subject-2", Address);
        var result = await SignInAsync(token: "second-token");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(SignInWithExternalProviderHandler.SignInFailedError);

        // The original link is untouched: a refusal must not half-apply.
        var account = await _accounts.GetByEmailAsync(Email.Create(Address));
        account!.ExternalSubject.Should().Be(Subject);
    }

    // Google issues tokens for addresses it has not proved. Accepting one would stamp EmailVerifiedAt
    // on the strength of nothing, and every later reader would trust it.
    [Fact]
    public async Task An_unverified_address_is_refused_and_creates_nothing()
    {
        _google.Accepting(Token, Subject, Address, emailVerified: false);

        var result = await SignInAsync();

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(SignInWithExternalProviderHandler.SignInFailedError);
        (await _accounts.GetByEmailAsync(Email.Create(Address))).Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_provider_is_refused_without_consulting_the_one_configured()
    {
        _google.Accepting(Token, Subject, Address);

        var result = await SignInAsync(provider: "facebook");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(SignInWithExternalProviderHandler.SignInFailedError);
        _google.VerifyCount.Should().Be(0, "a token for another provider must not be handed to this one");
    }

    [Fact]
    public async Task An_unconfigured_provider_is_refused()
    {
        _google.Accepting(Token, Subject, Address);
        _google.IsConfigured = false;

        (await SignInAsync()).IsSuccess.Should().BeFalse();
        _google.VerifyCount.Should().Be(0);
    }

    [Fact]
    public async Task A_token_the_provider_rejects_is_refused()
    {
        var result = await SignInAsync(token: "never-registered");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(SignInWithExternalProviderHandler.SignInFailedError);
    }

    // THE LOCKOUT IS NOT CONSULTED, and this is the test that says why it is deliberate. Lockout stops
    // password guessing; honouring it here would let anybody deny Google sign-in to a person by
    // spamming wrong passwords at their address.
    [Fact]
    public async Task A_locked_account_can_still_sign_in_externally_and_the_lockout_is_cleared()
    {
        var account = await SeedPasswordAccountAsync();
        for (var attempt = 0; attempt < 5; attempt++)
            account.RecordFailedLogin();
        await _accounts.UpdateAsync(account);
        account.IsLocked.Should().BeTrue("the test needs the lock to actually be on");

        _google.Accepting(Token, Subject, Address);
        var result = await SignInAsync();

        result.IsSuccess.Should().BeTrue();
        (await _accounts.GetByIdAsync(account.Id))!.IsLocked.Should().BeFalse();
    }

    // A suspension is a decision this product made about the account, not a defence against a guesser,
    // so it survives a provider sign-in where the lockout does not.
    [Fact]
    public async Task A_suspended_account_is_refused()
    {
        var account = await SeedPasswordAccountAsync();
        account.Suspend();
        await _accounts.UpdateAsync(account);
        _google.Accepting(Token, Subject, Address);

        var result = await SignInAsync();

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(SignInWithExternalProviderHandler.AccountNotActiveError);
    }

    [Fact]
    public async Task A_successful_sign_in_stores_a_refresh_token()
    {
        _google.Accepting(Token, Subject, Address);

        var result = await SignInAsync();

        result.IsSuccess.Should().BeTrue();
        (await _refreshTokens.GetByTokenAsync(result.Value!.RefreshToken.Token)).Should().NotBeNull();
    }
}
