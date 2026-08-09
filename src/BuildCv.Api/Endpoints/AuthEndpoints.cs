using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Observability;
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
            // IsDefined here is DEFENCE IN DEPTH, not a fix for a reachable corruption, and the
            // difference is worth stating so nobody deletes it as redundant.
            //
            // Account.Role is an int-backed enum on a tinyint column exactly like the membership role
            // below, so an undefined value would be just as durable — but it never reaches the column,
            // because RegisterAccountHandler.IsSelfAssignable is `role is Candidate or Recruiter` and an
            // undefined value is neither. Measured: role "-1" answered 400 before this line and answers
            // 400 after it; only the detail changes, from "Role is not available for self-registration."
            // to "Invalid role.", which is what EnumGuardTests asserts.
            //
            // What this line buys is that the refusal stops depending on an allow-list written to answer
            // a different question. IsSelfAssignable exists to decide what a stranger may grant
            // themselves; rewrite it as `role != Role.Admin` — the obvious edit the day a third role is
            // added — and an undefined value goes straight through to the tinyint. The same applies to
            // the next endpoint that parses a Role.
            //
            // Undefined values only: "Candidate,Recruiter" is 0|1 = Recruiter and "+1" is Recruiter,
            // both defined members, both still accepted. See the membership route for why they are left
            // reachable rather than narrowed at one site.
            var role = Role.Candidate;
            if (request.Role is not null
                && (!Enum.TryParse(request.Role, ignoreCase: true, out role) || !Enum.IsDefined(role)))
            {
                return Results.Problem(detail: "Invalid role.", statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await handler.Handle(
                new RegisterAccountCommand(request.Email, request.Password, role), cancellationToken);

            if (result.IsSuccess)
                AuditLog.Log(logger, "register_success", new AccountId(result.Value!.Id), httpContext, request.Email);

            // /v1/auth/me, NOT /v1/auth/accounts/{id}. The old Location named a route that is mapped
            // nowhere, so a client following the 201 convention got a routing 404 — and it is a ROUTING
            // 404, which looks exactly like a handler's, which is why nothing in the suite noticed.
            //
            // Pointed at the existing route rather than fixed by mapping the missing one. The only
            // account a caller of this endpoint can be following the header for is the one it just
            // created, and /v1/auth/me IS that resource: GetAccountQuery takes a requester and a target
            // and this route passes the same id for both. A by-id route would be a second way to say
            // that, with its own authorization to keep right — GetAccountHandler already admits
            // Role.Admin reading someone else, so the by-id surface would be strictly wider than the
            // 201 that pointed at it, for no caller that exists.
            //
            // THE ACCEPTED COST: following the header immediately answers 401, because registering does
            // not log you in — no cookie is set here and no token is returned. That is the honest
            // failure of an unauthenticated read, and a caller acts on it by calling /v1/auth/login,
            // whereas the 404 it replaces named a resource that did not exist at any credential.
            return result.ToHttpResult(dto => Results.Created("/v1/auth/me", AccountResponse.From(dto)));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Auth)
        .Produces<AccountResponse>(StatusCodes.Status201Created)
        .ProducesResultProblems()
        .ProducesProblem(StatusCodes.Status429TooManyRequests);

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
        .RequireRateLimiting(RateLimitPolicies.Auth)
        .Produces<TokenResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesProblem(StatusCodes.Status429TooManyRequests);

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
        .RequireRateLimiting(RateLimitPolicies.Auth)
        .Produces<TokenResponse>(StatusCodes.Status200OK)
        // 401 is returned by this route DIRECTLY, not through ToHttpResult: a request arriving with no
        // refresh cookie is refused before any handler runs.
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesResultProblems()
        .ProducesProblem(StatusCodes.Status429TooManyRequests);

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
            // revoke is exactly when a false "you are logged out" is most dangerous.
            //
            // Narrow by construction: this branch only sees what RevokeSessionsHandler converts,
            // i.e. DomainException and ArgumentException. A DbUpdateException or SqlException from
            // a real store escapes the handler entirely and GlobalExceptionHandler answers 500
            // instead — which lands in the same place, because that throw also precedes
            // ClearTokens. Unreachable with the in-memory repository; pinned by
            // SessionTerminationTests.Logout_WhenRevocationFails_Returns500AndLeavesTheCookiesInPlace.
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
        .RequireRateLimiting(RateLimitPolicies.Logout)
        .Produces(StatusCodes.Status204NoContent)
        // 500 is a real, documented answer here, not an accident: a revocation that FAILS leaves the
        // cookies in place and says so, because a store that cannot revoke is exactly when a false
        // "you are logged out" is most dangerous.
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Throttled per account instead of through the per-IP auth window: see
        // PasswordChangeRateLimiter for why sharing that window was both unfair and useless here.
        group.MapPost("/change-password", async Task<IResult> (
            ChangePasswordRequest request,
            ICommandHandler<ChangePasswordCommand, Result<AccountDto>> handler,
            PasswordChangeRateLimiter rateLimiter,
            BuildCvMetrics metrics,
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
                // Counted here, not in the middleware's OnRejected: PasswordChangeRateLimiter is
                // acquired inside the endpoint because UseRateLimiter runs before UseAuthentication and
                // a policy partitioner would have no principal to key on.
                metrics.ThrottleRejection(ThrottlePolicies.PasswordChange);
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

            return result.ToHttpResult(dto => Results.Ok(AccountResponse.From(dto)));
        })
        .Produces<AccountResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

        group.MapGet("/me", async (
            HttpContext httpContext,
            IQueryHandler<GetAccountQuery, Result<AccountDto>> handler,
            CancellationToken cancellationToken) =>
        {
            var requester = httpContext.User.GetAccountId();
            var result = await handler.Handle(new GetAccountQuery(requester, requester), cancellationToken);
            return result.ToHttpResult(dto => Results.Ok(AccountResponse.From(dto)));
        })
        .Produces<AccountResponse>(StatusCodes.Status200OK)
        .ProducesResultProblems()
        .ProducesAuthProblems();

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
        // the `expiresIn` field returned by /v1/auth/login and /v1/auth/refresh, and re-fetch this
        // token afterwards.
        group.MapGet("/antiforgery", (IAntiforgery antiforgery, HttpContext httpContext) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            return Results.Ok(new AntiforgeryTokenResponse(tokens.RequestToken!));
        })
        .Produces<AntiforgeryTokenResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
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
