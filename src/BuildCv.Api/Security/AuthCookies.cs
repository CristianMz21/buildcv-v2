using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Security;

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

        // Both cookies live for the session, i.e. until the refresh token expires. The access
        // cookie used to expire with the JWT it carries, which meant the browser silently deleted
        // the only credential naming the account roughly the moment the token went stale — so an
        // idle user pressing "log out" arrived anonymous and nothing got revoked. The JWT's own
        // `exp` is the security control and is still enforced server side on every scheme except
        // the logout-only one; the cookie's Expires is just client-side housekeeping, and tying it
        // to `exp` bought nothing while breaking session termination.
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
