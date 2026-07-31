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

        group.MapPost("/logout", (HttpContext httpContext, ILogger<Program> logger) =>
        {
            AuthCookies.ClearTokens(httpContext);
            AuditLog.Log(logger, "logout", httpContext.User.GetAccountIdOrNull(), httpContext);
            return Results.NoContent();
        });

        group.MapPost("/change-password", async Task<IResult> (
            ChangePasswordRequest request,
            ICommandHandler<ChangePasswordCommand, Result<AccountDto>> handler,
            ILogger<Program> logger,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new ChangePasswordCommand(httpContext.User.GetAccountId(), request.CurrentPassword, request.NewPassword),
                cancellationToken);

            if (result.IsSuccess)
                AuditLog.Log(logger, "password_changed", new AccountId(result.Value!.Id), httpContext);

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

        group.MapGet("/antiforgery", (IAntiforgery antiforgery, HttpContext httpContext) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            return Results.Ok(new AntiforgeryTokenResponse(tokens.RequestToken!));
        })
        .AllowAnonymous();

        return group;
    }
}
