namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;

public sealed class FakeRefreshTokenRepository : IRefreshTokenRepository
{
    private readonly List<RefreshToken> _tokens = [];

    public Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tokens.FirstOrDefault(t => t.Token == token));

    public Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default)
    {
        _tokens.Add(refreshToken);
        return Task.CompletedTask;
    }

    public Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        _tokens.RemoveAll(t => t.Token == token);
        return Task.CompletedTask;
    }
}
