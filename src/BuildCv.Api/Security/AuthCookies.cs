using BuildCv.Domain.Identity;

namespace BuildCv.Api.Security;

public static class AuthCookies
{
    public const string AccessTokenCookie = "access_token";
    public const string RefreshTokenCookie = "refresh_token";
    public const string RefreshCookiePath = "/auth/refresh";

    // Development is the only environment allowed to serve auth cookies without Secure, so that
    // local http debugging keeps working. Every other environment — Staging, QA, Preview,
    // Production — gets the Secure attribute unconditionally. Both helpers must agree: gating
    // one on Production and the other on Development previously left the antiforgery cookie
    // Secure while the auth cookies were not.
    public static CookieSecurePolicy SecurePolicyFor(IHostEnvironment environment) =>
        environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

    private static bool IsSecure(HttpContext context) =>
        !context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();

    public static void SetTokens(HttpContext context, AuthResult auth)
    {
        ArgumentNullException.ThrowIfNull(auth);

        var secure = IsSecure(context);

        // Both cookies are PERSISTENT and share the refresh token's expiry — 30 days on disk by
        // default, not a session cookie. The access cookie used to carry
        // Expires = now + AccessTokenMinutes, which meant the browser deleted the only credential
        // naming the account at the same instant the JWT went stale, so an idle user pressing
        // "log out" arrived anonymous and nothing got revoked. The JWT's own `exp` is the security
        // control and is still enforced on every scheme except the logout-only one; the cookie's
        // Expires is client-side housekeeping, and tying it to `exp` bought nothing while breaking
        // session termination.
        //
        // Two accepted costs, both documented in CLAUDE.md:
        //   1. A stale JWT now sits on disk for up to 30 days. JWTs are signed, not encrypted, so
        //      `sub`, `email` and `role` are readable by anyone with filesystem access for that
        //      month. The refresh cookie is already a 30-day on-disk artifact granting strictly
        //      more capability, so this does not open a new class of exposure.
        //   2. Because CsrfGuardMiddleware gates on cookie PRESENCE, an idle cookie client's next
        //      unsafe request to any non-exempt route now answers 403 (CSRF) where it used to
        //      answer 401. Clients must refresh proactively off `expiresIn`, not reactively off
        //      401. A session cookie would not avoid this — an idle-but-open browser hits the same
        //      flip — so it is inherent to the cookie outliving the JWT, which the logout fix
        //      requires.
        context.Response.Cookies.Append(AccessTokenCookie, auth.AccessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = auth.RefreshToken.ExpiresAt
        });

        context.Response.Cookies.Append(RefreshTokenCookie, auth.RefreshToken.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = auth.RefreshToken.ExpiresAt
        });
    }

    public static void ClearTokens(HttpContext context)
    {
        var secure = IsSecure(context);
        var expired = DateTimeOffset.UnixEpoch;

        context.Response.Cookies.Append(AccessTokenCookie, string.Empty, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expired
        });

        context.Response.Cookies.Append(RefreshTokenCookie, string.Empty, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = expired
        });
    }
}
