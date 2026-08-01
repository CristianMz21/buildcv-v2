using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Identity;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Infrastructure.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
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

            AuthCookies.SetTokens(httpContext, result.Value!);
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

            AuthCookies.SetTokens(httpContext, result.Value!);
            AuditLog.Log(logger, "refresh_success", result.Value!.AccountId, httpContext);
            return Results.Ok(new TokenResponse(result.Value!.AccessToken, jwt.Value.AccessTokenMinutes * 60));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Auth);

        // AllowAnonymous is deliberate. The fallback authorization policy would answer 401 the
        // moment the access token expired, which is precisely when a user wants out — and a 401
        // leaves both cookies in the browser with the refresh token still live in the store, so
        // requiring authentication makes the security action unavailable exactly when it matters.
        // Cookies are cleared whenever there is nothing left to revoke — including for a caller
        // this API cannot identify at all, which is still a valid request to drop this browser's
        // credentials and leaks nothing. The one exception is a revocation that actively failed;
        // see below. CsrfGuardMiddleware still covers this route (it is not in ExemptPaths), so a
        // cross-site request cannot force-revoke a victim's sessions.
        group.MapPost("/logout", async Task<IResult> (
            HttpContext httpContext,
            ICommandHandler<RevokeSessionsCommand, Result> handler,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            // Authenticated explicitly against the lifetime-ignoring scheme rather than read off
            // httpContext.User, because the common logout is "idle tab, token already expired".
            // Signature, issuer and audience are still validated there, so this still requires a
            // token this API issued for this account.
            var authentication =
                await httpContext.AuthenticateAsync(AuthenticationSchemes.ExpiredAccessTokenAllowed);
            var accountId = authentication.Principal?.GetAccountIdOrNull();

            var revoked = accountId is null
                ? Result.Success()
                : await handler.Handle(new RevokeSessionsCommand(accountId), cancellationToken);

            // Cookies are cleared only once revocation actually succeeded. Answering 204 after a
            // failed revocation would tell the client the session ended while the refresh token is
            // still live in the store; clearing the cookies on the way out would additionally take
            // away the credential needed to retry, leaving the live session unreachable until it
            // expires on its own. So: 500, cookies intact, retry possible. The trade-off is that a
            // failed logout leaves this browser armed — deliberate, because a store that cannot
            // revoke is exactly when a false "you are logged out" is most dangerous. Unreachable
            // with the in-memory repository; reachable the moment an EF store sits behind the port.
            if (!revoked.IsSuccess)
            {
                AuditLog.Log(logger, "logout_revoke_failure", accountId, httpContext);
                return Results.Problem(
                    detail: "Sessions could not be revoked.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            AuthCookies.ClearTokens(httpContext);
            AuditLog.Log(logger, "logout", accountId, httpContext);
            return Results.NoContent();
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Logout);

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

        // Client contract: fetch this token AFTER logging in, and re-fetch it whenever the
        // principal this API sees for your requests changes — login, logout, account switch, AND
        // access-token expiry. ASP.NET Core binds the request token to the principal that was
        // authenticated when the token was issued, so a mismatch is rejected with 403 in either
        // direction: a token obtained while anonymous fails once the caller holds a valid auth
        // cookie, and a token obtained while authenticated fails once the caller's access token
        // has expired and it reads as anonymous again.
        //
        // Expiry is the trigger clients get wrong. The access-token cookie outlives the JWT it
        // carries (see AuthCookies.SetTokens), so an idle client keeps sending a cookie that no
        // longer authenticates. CsrfGuardMiddleware gates on cookie PRESENCE, so the next unsafe
        // request enters antiforgery validation as anonymous holding an authenticated-bound token
        // and gets 403 — not the 401 a "retry on 401" loop is waiting for. Refresh proactively off
        // the `expiresIn` field returned by /auth/login and /auth/refresh, and re-fetch this token
        // afterwards.
        group.MapGet("/antiforgery", (IAntiforgery antiforgery, HttpContext httpContext) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            return Results.Ok(new AntiforgeryTokenResponse(tokens.RequestToken!));
        })
        .WithSummary("Issues a CSRF request token bound to the caller's current principal.")
        .WithDescription(
            "Re-fetch after every change of principal: login, logout, account switch, and access-token "
            + "expiry. A token bound to a different principal than the one the request authenticates as "
            + "is rejected with 403 in both directions. Because the access-token cookie outlives the JWT "
            + "it carries, an idle client's next unsafe request answers 403 rather than 401 — refresh "
            + "proactively off `expiresIn` instead of reacting to 401, then re-fetch this token.")
        .AllowAnonymous();

        return group;
    }
}
