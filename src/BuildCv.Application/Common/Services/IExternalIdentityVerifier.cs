namespace BuildCv.Application.Common.Services;

using BuildCv.Domain.Common.ValueObjects;

/// <summary>
/// What an external identity provider asserts about somebody, once their token has been verified.
/// </summary>
/// <param name="Subject">
/// The provider's stable identifier for the person. Never equal to an email — an address can be
/// reassigned or changed and this cannot.
/// </param>
/// <param name="Email">The address the provider states, unvalidated by this type.</param>
/// <param name="EmailVerified">
/// The provider's own claim that it proved this address. <b>Carried rather than acted on</b>: whether an
/// unverified address may sign in is a product decision, and a verifier that quietly refused would move
/// it somewhere no reader of the use case can see.
/// </param>
public sealed record ExternalIdentity(string Subject, string Email, bool EmailVerified);

/// <summary>
/// Verifies an identity token issued by an external provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>This API verifies the token itself and does not take the caller's word for who somebody is.</b>
/// The BFF forwards the provider's token untouched; if it sent a decoded <c>{ email }</c> instead, then
/// anything able to reach the internal ingress could assert any identity, and the BFF would become the
/// authority on who a person is. It has never been that for anything else here, and an authentication
/// endpoint is the worst place to start.
/// </para>
/// <para>
/// The adapter therefore needs the provider's PUBLIC keys and the expected audience — never the client
/// secret, which belongs only to the component that exchanges an authorisation code.
/// </para>
/// <para>
/// It returns <see cref="Result{T}"/> rather than throwing: a token that fails to verify is an ordinary
/// outcome of an authentication attempt, and the endpoint has to answer something either way. What it
/// must never do is explain WHICH check failed to the caller — see the remark on the use case.
/// </para>
/// </remarks>
public interface IExternalIdentityVerifier
{
    /// <summary>The provider this verifier speaks for, matched case-insensitively against the request.</summary>
    string Provider { get; }

    /// <summary>Whether the server is configured to verify this provider's tokens at all.</summary>
    bool IsConfigured { get; }

    Task<Result<ExternalIdentity>> VerifyAsync(string idToken, CancellationToken cancellationToken = default);
}
