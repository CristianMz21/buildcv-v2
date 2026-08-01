using BuildCv.Application.Identity;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Identity;
using FluentAssertions;

namespace BuildCv.Application.Tests.Identity;

public class RevokeSessionsHandlerTests
{
    private readonly FakeRefreshTokenRepository _refreshTokens = new();
    private readonly RevokeSessionsHandler _handler;

    public RevokeSessionsHandlerTests() => _handler = new RevokeSessionsHandler(_refreshTokens);

    private async Task<RefreshToken> SeedTokenAsync(AccountId accountId, char fill)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var token = RefreshToken.Create(new string(fill, 86), accountId, createdAt, createdAt.AddDays(30));
        await _refreshTokens.AddAsync(token);
        return token;
    }

    [Fact]
    public async Task RevokeSessions_removes_every_token_for_the_account()
    {
        var accountId = AccountId.New();
        var first = await SeedTokenAsync(accountId, 'a');
        var second = await SeedTokenAsync(accountId, 'b');

        var result = await _handler.Handle(new RevokeSessionsCommand(accountId));

        result.IsSuccess.Should().BeTrue();
        (await _refreshTokens.GetByTokenAsync(first.Token)).Should().BeNull();
        (await _refreshTokens.GetByTokenAsync(second.Token)).Should().BeNull();
    }

    [Fact]
    public async Task RevokeSessions_leaves_other_accounts_signed_in()
    {
        var target = AccountId.New();
        await SeedTokenAsync(target, 'a');
        var bystander = await SeedTokenAsync(AccountId.New(), 'b');

        await _handler.Handle(new RevokeSessionsCommand(target));

        (await _refreshTokens.GetByTokenAsync(bystander.Token)).Should().Be(bystander);
    }

    [Fact]
    public async Task RevokeSessions_without_any_stored_token_succeeds()
    {
        var result = await _handler.Handle(new RevokeSessionsCommand(AccountId.New()));

        result.IsSuccess.Should().BeTrue();
    }
}
