using BuildCv.Application.Identity;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// The restart-durability proof, automated.
//
// Everything below is the real thing: the real RegisterAccountHandler and LoginHandler, the real
// Argon2id hasher, the real TokenService, the real EF repositories, a real SQL Server. Each step gets a
// FRESH DbContext, which is the closest an in-process test can get to "the process was restarted" —
// nothing can be answered out of a change tracker that a previous step populated.
//
// What it actually pins is the whole encrypted-lookup chain end to end. Registration writes an encrypted
// address and a blind-index digest; login has to find that row from a plaintext string typed by a user.
// Any break in between — a mismatched AAD context, a normalization difference, a lookup on the encrypted
// column — reads as "invalid credentials", which is indistinguishable from a wrong password.
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuthPersistenceTests
{
    private const string Password = "Str0ng!Password#2026";

    private readonly SqlServerFixture _fixture;

    public AuthPersistenceTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AnAccountRegisteredInOneContext_CanLogInFromAnother()
    {
        var email = UniqueEmail("durable");

        await using (var registration = _fixture.NewApplicationContext())
        {
            var result = await Register(registration).Handle(new RegisterAccountCommand(email, Password));
            result.IsSuccess.Should().BeTrue(result.Error);
        }

        await using var session = _fixture.NewApplicationContext();
        var login = await Login(session).Handle(new LoginCommand(email, Password));

        login.IsSuccess.Should().BeTrue(login.Error);
        login.Value!.AccessToken.Should().NotBeNullOrWhiteSpace();
        login.Value.RefreshToken.Token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task LoggingInWithTheWrongPassword_FailsWithoutRevealingWhetherTheAccountExists()
    {
        var email = UniqueEmail("wrongpassword");

        await using (var registration = _fixture.NewApplicationContext())
            await Register(registration).Handle(new RegisterAccountCommand(email, Password));

        await using var session = _fixture.NewApplicationContext();
        var login = await Login(session).Handle(new LoginCommand(email, "Wr0ng!Password#2026"));

        login.IsSuccess.Should().BeFalse();
        login.Error.Should().Be("Invalid credentials.");
    }

    [Fact]
    public async Task RegisteringAnAddressTwice_IsRejectedByTheDuplicateCheck()
    {
        var email = UniqueEmail("twice");

        await using (var first = _fixture.NewApplicationContext())
            (await Register(first).Handle(new RegisterAccountCommand(email, Password))).IsSuccess.Should().BeTrue();

        await using var second = _fixture.NewApplicationContext();
        var result = await Register(second).Handle(new RegisterAccountCommand(email, Password));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Email is already registered.");
    }

    // The refresh rotation across three separate contexts. The old token has to stop working the moment
    // it is exchanged — that is the whole point of RevokeAsync being a tombstone — and the new one has to
    // work from a context that never saw either of them.
    [Fact]
    public async Task RefreshingRotatesTheToken_AndTheSpentOneStopsWorking()
    {
        var email = UniqueEmail("refresh");

        await using (var registration = _fixture.NewApplicationContext())
            await Register(registration).Handle(new RegisterAccountCommand(email, Password));

        string issued;
        await using (var session = _fixture.NewApplicationContext())
        {
            var login = await Login(session).Handle(new LoginCommand(email, Password));
            issued = login.Value!.RefreshToken.Token;
        }

        string rotated;
        await using (var refresh = _fixture.NewApplicationContext())
        {
            var result = await Refresh(refresh).Handle(new RefreshAccessTokenCommand(issued));
            result.IsSuccess.Should().BeTrue(result.Error);
            rotated = result.Value!.RefreshToken.Token;
            rotated.Should().NotBe(issued);
        }

        await using var replay = _fixture.NewApplicationContext();
        var reused = await Refresh(replay).Handle(new RefreshAccessTokenCommand(issued));

        reused.IsSuccess.Should().BeFalse("a revoked refresh token must not keep minting access tokens");
        reused.Error.Should().Be("Invalid refresh token.");

        await using var accepted = _fixture.NewApplicationContext();
        (await Refresh(accepted).Handle(new RefreshAccessTokenCommand(rotated))).IsSuccess.Should().BeTrue();
    }

    private static RegisterAccountHandler Register(BuildCvDbContext context) =>
        new(TestRepositories.Accounts(context), new PasswordHasher());

    private static LoginHandler Login(BuildCvDbContext context) =>
        new(
            TestRepositories.Accounts(context),
            TestRepositories.RefreshTokens(context),
            new PasswordHasher(),
            NewTokenService(),
            TimeProvider.System);

    private static RefreshAccessTokenHandler Refresh(BuildCvDbContext context) =>
        new(
            TestRepositories.RefreshTokens(context),
            TestRepositories.Accounts(context),
            NewTokenService(),
            TimeProvider.System);

    private static TokenService NewTokenService() =>
        new(Options.Create(new JwtSettings
        {
            SigningKey = "test-signing-key-min-32-characters-long-0123456789",
            Issuer = "buildcv-api",
            Audience = "buildcv-bff"
        }));

    private static string UniqueEmail(string label) => $"auth.{label}.{Guid.NewGuid():N}@example.com";
}
