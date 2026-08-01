using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Identity;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace BuildCv.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", async Task<IResult> (
            RegisterRequest request,
            ICommandHandler<RegisterAccountCommand, Result<AccountDto>> handler,
            ILogger<Program> logger,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var role = Role.Candidate;
            if (request.Role is not null
                && !Enum.TryParse(request.Role, ignoreCase: true, out role))
            {
                return Results.Problem(detail: "Invalid role.", statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await handler.Handle(
                new RegisterAccountCommand(request.Email, request.Password, role), cancellationToken);

            if (result.IsSuccess)
                AuditLog.Log(logger, "register_success", new AccountId(result.Value!.Id), httpContext, request.Email);

            return result.ToHttpResult(dto => Results.Created($"/auth/accounts/{dto.Id}", dto));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/login", async Task<IResult> (
            LoginRequest request,
            ICommandHandler<LoginCommand, Result<AuthResult>> handler,
            IOptions<JwtSettings> jwt,
            ILogger<Program> logger,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new LoginCommand(request.Email, request.Password), cancellationToken);

            if (!result.IsSuccess)
            {
                AuditLog.Log(logger, "login_failure", null, httpContext, request.Email);
                return result.ToHttpResult();
            }

            AuthCookies.SetTokens(httpContext, result.Value!, jwt.Value);
            AuditLog.Log(logger, "login_success", result.Value!.AccountId, httpContext, request.Email);
            return Results.Ok(new TokenResponse(result.Value!.AccessToken, jwt.Value.AccessTokenMinutes * 60));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/refresh", async Task<IResult> (
            HttpContext httpContext,
            ICommandHandler<RefreshAccessTokenCommand, Result<AuthResult>> handler,
            IOptions<JwtSettings> jwt,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            if (!httpContext.Request.Cookies.TryGetValue(AuthCookies.RefreshTokenCookie, out var refreshToken))
            {
                AuditLog.Log(logger, "refresh_failure", null, httpContext);
                return Results.Problem(detail: "Refresh token is missing.", statusCode: StatusCodes.Status401Unauthorized);
            }

            var result = await handler.Handle(new RefreshAccessTokenCommand(refreshToken), cancellationToken);

            if (!result.IsSuccess)
            {
                AuditLog.Log(logger, "refresh_failure", null, httpContext);
                return result.ToHttpResult();
            }

            AuthCookies.SetTokens(httpContext, result.Value!, jwt.Value);
            AuditLog.Log(logger, "refresh_success", result.Value!.AccountId, httpContext);
            return Results.Ok(new TokenResponse(result.Value!.AccessToken, jwt.Value.AccessTokenMinutes * 60));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Auth);

        // AllowAnonymous is deliberate. The fallback authorization policy would answer 401 the
        // moment the access token expired, which is precisely when a user wants out — and a 401
        // leaves both cookies in the browser with the refresh token still live in the store, so
        // requiring authentication makes the security action unavailable exactly when it matters.
        // Authentication middleware still runs, so a caller presenting a valid token is
        // identified and every refresh token on that account is revoked. Cookies are cleared
        // unconditionally and the answer is always 204: an anonymous logout is a no-op that leaks
        // nothing. CsrfGuardMiddleware still covers this route (it is not in ExemptPaths), so a
        // cross-site request cannot force-revoke a victim's sessions.
        group.MapPost("/logout", async Task<IResult> (
            HttpContext httpContext,
            ICommandHandler<RevokeSessionsCommand, Result> handler,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var accountId = httpContext.User.GetAccountIdOrNull();
            var revoked = accountId is null
                ? null
                : await handler.Handle(new RevokeSessionsCommand(accountId), cancellationToken);

            AuthCookies.ClearTokens(httpContext);
            AuditLog.Log(
                logger,
                revoked is { IsSuccess: false } ? "logout_revoke_failure" : "logout",
                accountId,
                httpContext);
            return Results.NoContent();
        })
        .AllowAnonymous();

        // Throttled per account instead of through the per-IP auth window: see
        // PasswordChangeRateLimiter for why sharing that window was both unfair and useless here.
        group.MapPost("/change-password", async Task<IResult> (
            ChangePasswordRequest request,
            ICommandHandler<ChangePasswordCommand, Result<AccountDto>> handler,
            PasswordChangeRateLimiter rateLimiter,
            ILogger<Program> logger,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var accountId = httpContext.User.GetAccountId();

            using var lease = await rateLimiter.AcquireAsync(accountId, cancellationToken);
            if (!lease.IsAcquired)
            {
                RateLimitResponse.SetRetryAfter(httpContext.Response, lease);
                AuditLog.Log(logger, "password_change_throttled", accountId, httpContext);
                return Results.Problem(
                    detail: "Too many password change attempts.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var result = await handler.Handle(
                new ChangePasswordCommand(accountId, request.CurrentPassword, request.NewPassword),
                cancellationToken);

            if (result.IsSuccess)
            {
                // The handler just revoked every refresh token on this account, including the one
                // in the caller's own cookie. Clearing both cookies keeps the browser's view
                // honest instead of leaving it holding credentials the server no longer accepts.
                AuthCookies.ClearTokens(httpContext);
                AuditLog.Log(logger, "password_changed", new AccountId(result.Value!.Id), httpContext);
            }

            return result.ToHttpResult();
        });

        group.MapGet("/me", async (
            HttpContext httpContext,
            IQueryHandler<GetAccountQuery, Result<AccountDto>> handler,
            CancellationToken cancellationToken) =>
        {
            var requester = httpContext.User.GetAccountId();
            var result = await handler.Handle(new GetAccountQuery(requester, requester), cancellationToken);
            return result.ToHttpResult();
        });

        // Client contract: fetch this token AFTER logging in, and re-fetch it after every login,
        // logout, or account switch. ASP.NET Core binds the request token to the principal that
        // was authenticated when the token was issued, so a token obtained while anonymous is
        // rejected with 403 once the caller holds an auth cookie.
        group.MapGet("/antiforgery", (IAntiforgery antiforgery, HttpContext httpContext) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            return Results.Ok(new AntiforgeryTokenResponse(tokens.RequestToken!));
        })
        .AllowAnonymous();

        return group;
    }
}
