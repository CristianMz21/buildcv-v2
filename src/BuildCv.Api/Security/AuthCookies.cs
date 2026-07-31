using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Security;

namespace BuildCv.Api.Security;

public static class AuthCookies
{
    public const string AccessTokenCookie = "access_token";
    public const string RefreshTokenCookie = "refresh_token";
    public const string RefreshCookiePath = "/auth/refresh";

    public static CookieSecurePolicy SecurePolicyFor(IHostEnvironment environment) =>
        environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;

    // CookieOptions.Secure maps to the Secure attribute: Always in production, omitted
    // outside production so local dev and tests work over plain http.
    private static bool IsSecure(HttpContext context) =>
        context.RequestServices.GetRequiredService<IHostEnvironment>().IsProduction();

    public static void SetTokens(HttpContext context, AuthResult auth, JwtSettings settings)
    {
        var secure = IsSecure(context);

        context.Response.Cookies.Append(AccessTokenCookie, auth.AccessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddMinutes(settings.AccessTokenMinutes)
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
