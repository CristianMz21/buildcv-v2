using System.Text;
using BuildCv.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace BuildCv.Api.Security;

public static class JwtBearerConfiguration
{
    /// <summary>
    /// Shared validation setup for every JWT scheme in the app.
    /// </summary>
    /// <param name="validateLifetime">
    /// Only <see cref="AuthenticationSchemes.ExpiredAccessTokenAllowed"/> passes <c>false</c>.
    /// Everything else — signature, issuer, audience — is still enforced there, so a token that
    /// fails this check is authentic and merely stale, not forged.
    /// </param>
    public static void Configure(JwtBearerOptions options, JwtSettings jwtSettings, bool validateLifetime)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(jwtSettings);

        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
            ValidateLifetime = validateLifetime,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                if (string.IsNullOrWhiteSpace(authorization)
                    && context.Request.Cookies.TryGetValue(AuthCookies.AccessTokenCookie, out var cookieToken))
                {
                    context.Token = cookieToken;
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                context.NoResult();
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "about:blank",
                    title = "Unauthorized",
                    status = StatusCodes.Status401Unauthorized
                });
            }
        };
    }
}
