using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Application.Tests.Fakes;

/// <summary>
/// Stands in for a real provider by keying answers off the token string.
/// </summary>
/// <remarks>
/// It verifies nothing, which is the point: what the use case decides on top of a verified identity is
/// a different question from whether a signature is genuine, and mixing them would make every test here
/// depend on a JWKS fetch. The signature side belongs to the adapter's own tests.
/// </remarks>
internal sealed class FakeExternalIdentityVerifier(string provider = "google") : IExternalIdentityVerifier
{
    private readonly Dictionary<string, ExternalIdentity> _identities = [];

    public string Provider { get; } = provider;

    public bool IsConfigured { get; set; } = true;

    /// <summary>How many times a token was submitted — proof that a path really consulted the provider.</summary>
    public int VerifyCount { get; private set; }

    public FakeExternalIdentityVerifier Accepting(
        string token, string subject, string email, bool emailVerified = true)
    {
        _identities[token] = new ExternalIdentity(subject, email, emailVerified);
        return this;
    }

    public Task<Result<ExternalIdentity>> VerifyAsync(
        string idToken, CancellationToken cancellationToken = default)
    {
        VerifyCount++;
        return Task.FromResult(
            _identities.TryGetValue(idToken, out var identity)
                ? Result<ExternalIdentity>.Success(identity)
                : Result<ExternalIdentity>.Failure("Could not verify that token."));
    }
}
