namespace BuildCv.Infrastructure.Security;

using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Settings for verifying Google identity tokens, bound from <c>Authentication:Google</c>.
/// </summary>
public sealed class GoogleAuthSettings
{
    public const string SectionName = "Authentication:Google";

    /// <summary>
    /// The OAuth client id, which is also the <c>aud</c> every accepted token must carry.
    /// </summary>
    /// <remarks>
    /// <b>Only the public half.</b> Verifying a signature needs Google's public keys and the expected
    /// audience; the client SECRET belongs exclusively to whatever exchanges an authorisation code, and
    /// this process does not. If a deployment ever puts the secret here, it has given a component more
    /// than it can use — and one more place for it to leak from.
    /// </remarks>
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>
/// Verifies Google identity tokens against Google's published keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>The keys are fetched, cached and rotated by <see cref="ConfigurationManager{T}"/> rather than by
/// this class.</b> Google rotates its signing keys on its own schedule and publishes them through a
/// discovery document; a hand-rolled cache is the kind of code that works for weeks and then rejects
/// every token at once, at an hour nobody chose. The manager also keeps a last-known-good configuration,
/// so a transient failure to reach Google does not immediately become a failure to sign anybody in.
/// </para>
/// <para>
/// <b>Registered as a singleton</b>, and that is load-bearing rather than an optimisation: a per-request
/// instance would re-fetch the discovery document on every sign-in, turning Google's endpoint into a
/// dependency of every request instead of a background refresh.
/// </para>
/// <para>
/// The failure message is the same for every cause and carries none of the detail — see the remark on
/// <c>SignInWithExternalProviderHandler</c>. The detail is logged by nothing here on purpose: a token is
/// a credential and its claims name a person, so it may not reach a log line (see the observability
/// rules in CLAUDE.md).
/// </para>
/// </remarks>
public sealed class GoogleIdentityVerifier : IExternalIdentityVerifier, IDisposable
{
    /// <summary>Google's discovery document, which names the JWKS endpoint and the valid issuers.</summary>
    public const string MetadataAddress = "https://accounts.google.com/.well-known/openid-configuration";

    /// <summary>
    /// Both spellings Google uses for <c>iss</c>. Tokens carry one or the other and both are correct;
    /// accepting only the one in the discovery document rejects real tokens.
    /// </summary>
    public static readonly string[] ValidIssuers = ["https://accounts.google.com", "accounts.google.com"];

    /// <summary>The one answer to every failure, so no caller learns which check refused it.</summary>
    public const string VerificationFailedError = "Could not verify that token.";

    private readonly GoogleAuthSettings _settings;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configuration;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly HttpClient? _httpClient;

    public GoogleIdentityVerifier(GoogleAuthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        // NOTHING IS CONSTRUCTED WHEN UNCONFIGURED, so a deployment that has not enabled Google sign-in
        // never opens a socket to it. IsConfigured is what the use case consults, and this keeps that
        // property true of the process rather than only of the answers.
        if (!IsConfigured)
            return;

        _httpClient = new HttpClient();
        _configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
            MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(_httpClient) { RequireHttps = true });
    }

    public string Provider => "google";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.ClientId);

    public async Task<Result<ExternalIdentity>> VerifyAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || _configuration is null || string.IsNullOrWhiteSpace(idToken))
            return Result<ExternalIdentity>.Failure(VerificationFailedError);

        try
        {
            var openIdConfiguration = await _configuration.GetConfigurationAsync(cancellationToken);

            var parameters = new TokenValidationParameters
            {
                // EVERY ONE OF THESE IS ON DELIBERATELY. A token that is correctly signed by Google and
                // issued for SOMEBODY ELSE'S application is a valid Google token — so without the
                // audience check, anyone with any Google app could mint tokens this server accepts.
                // That is the classic confused-deputy in this protocol and the audience is the whole
                // defence against it.
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = openIdConfiguration.SigningKeys,
                ValidateIssuer = true,
                ValidIssuers = ValidIssuers,
                ValidateAudience = true,
                ValidAudience = _settings.ClientId,
                ValidateLifetime = true,
                // Google's tokens are short-lived and clocks drift; the default five minutes is more
                // than this needs and is narrowed rather than accepted by omission.
                ClockSkew = TimeSpan.FromMinutes(1),
            };

            var result = await _handler.ValidateTokenAsync(idToken, parameters);
            if (!result.IsValid)
                return Result<ExternalIdentity>.Failure(VerificationFailedError);

            var subject = Claim(result, JwtRegisteredClaimNames.Sub);
            var email = Claim(result, JwtRegisteredClaimNames.Email);

            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
                return Result<ExternalIdentity>.Failure(VerificationFailedError);

            // GOOGLE SENDS THIS AS EITHER A BOOLEAN OR THE STRING "true", depending on the flow that
            // produced the token, and a reader that only understands one of them silently treats every
            // token of the other shape as unverified. Both are accepted; anything else is not true.
            var verified = result.Claims.TryGetValue("email_verified", out var raw)
                && raw switch
                {
                    bool flag => flag,
                    string text => bool.TryParse(text, out var parsed) && parsed,
                    _ => false,
                };

            return Result<ExternalIdentity>.Success(new ExternalIdentity(subject, email, verified));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reaching Google can fail, and a token can be malformed in ways that throw rather than
            // return invalid. Both are "could not verify", and neither is this server's fault to
            // explain to a caller.
            return Result<ExternalIdentity>.Failure(VerificationFailedError);
        }
    }

    private static string? Claim(TokenValidationResult result, string name) =>
        result.Claims.TryGetValue(name, out var value) ? value as string : null;

    public void Dispose() => _httpClient?.Dispose();
}
