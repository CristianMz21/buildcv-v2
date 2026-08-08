using System.Text;
using BuildCv.Api.Common;
using BuildCv.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
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
            // The most frequent error response this API produces — every expired session reaches it —
            // and it was the one that carried application/json for the longest. The assignment to
            // Response.ContentType that used to sit here never survived: the WriteAsJsonAsync overload
            // without an explicit contentType overwrites it. Passing it as an argument is the fix, and
            // ErrorContentTypeTests asserts the header rather than the body, which is why the defect
            // had gone unnoticed.
            //
            // The anonymous object it used to write was already a conformant RFC 7807 document — type,
            // title and status are all optional members — but it was the only error body in the API not
            // built from ProblemDetails. Measured: this ProblemDetails serialises to exactly the same
            // three properties in the same order, because the unset members are omitted rather than
            // written as null. Same bytes, one type.
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    new ProblemDetails
                    {
                        Type = "about:blank",
                        Title = "Unauthorized",
                        Status = StatusCodes.Status401Unauthorized
                    },
                    options: null,
                    contentType: ProblemDetailsContentType.Value);
            }
        };
    }
}
