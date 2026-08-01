using System.Text;
using System.Threading.RateLimiting;
using BuildCv.Api.Common;
using BuildCv.Api.Endpoints;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Services;
using BuildCv.Infrastructure;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
var forwardedHeaders = ForwardedHeadersConfiguration.Read(builder.Configuration);

// The environment name goes in because the persistence provider is chosen there: the in-memory store is
// allowed to be selected locally and must not be selectable on a deployed host by accident.
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.EnvironmentName);

// Overrides the UnknownCurrentUser that AddInfrastructure registers with TryAdd semantics. TryAdd
// no-ops once the service type is present and the last plain registration wins, so either order
// resolves this one — it is what puts a real account id into the CreatedBy / UpdatedBy / DeletedBy
// columns instead of NULL. Removing this line, or weakening it to TryAdd, silently reverts them.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

builder.Services.AddSingleton<PasswordChangeRateLimiter>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

// Bearer validation is configured through the options system so it resolves the very same
// JwtSettings instance that TokenService signs with. Reading configuration eagerly off the
// builder would capture values from the sources known at that moment and silently miss any
// provider added while the host is being built, desynchronizing the validation key from the
// signing key and rejecting every token issued by this API.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtSettings>>((options, jwtOptions) =>
    {
        var jwtSettings = jwtOptions.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
            ValidateLifetime = true,
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
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthorizationPolicies.Candidate, policy => policy.RequireRole("Candidate", "Recruiter", "Admin"))
    .AddPolicy(AuthorizationPolicies.Recruiter, policy => policy.RequireRole("Recruiter", "Admin"))
    .AddPolicy(AuthorizationPolicies.Admin, policy => policy.RequireRole("Admin"))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        RateLimitResponse.SetRetryAfter(context.HttpContext.Response, context.Lease);
        return ValueTask.CompletedTask;
    };
    // Both limiters partition on the peer address, which is only the real client when the app is
    // either directly exposed or running with Network:ForwardedHeaders configured for its proxies.
    options.AddPolicy(RateLimitPolicies.Auth, httpContext => RateLimitPartition.GetFixedWindowLimiter(
        RateLimitPartitions.ClientKey(httpContext),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            RateLimitPartitions.ClientKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "XSRF-TOKEN";
    options.Cookie.HttpOnly = false;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = AuthCookies.SecurePolicyFor(builder.Environment);
    options.HeaderName = CsrfGuardMiddleware.CsrfHeaderName;
});

if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddPolicy(CorsPolicies.Strict, policy => policy
        .WithOrigins(allowedOrigins)
        .AllowCredentials()
        .WithMethods("GET", "POST", "PUT", "DELETE")
        .WithHeaders("Authorization", "Content-Type", CsrfGuardMiddleware.CsrfHeaderName)));
}

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
// Before the catch-all: a storage conflict is a 409 the client can act on, and letting it fall through
// to the 500 handler would tell a caller whose only problem is a stale copy that the server is broken.
builder.Services.AddExceptionHandler<PersistenceExceptionHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddOpenApi();

var app = builder.Build();

// A startup action, not middleware: it runs to completion here, before the pipeline below is
// composed, so it neither occupies nor competes for a slot in it.
//
// Development convenience only, and narrow on purpose. Applying migrations from inside the application
// means the process that serves traffic also owns the schema; that is fine for a laptop and wrong for a
// deployment, where the migration is a separate, reviewable step that runs once rather than once per
// instance. Guarded three ways so turning it on anywhere else takes a deliberate act.
if (app.Environment.IsDevelopment()
    && PersistenceConfiguration.UsesSqlServer(app.Configuration)
    && PersistenceConfiguration.AutoMigrateEnabled(app.Configuration))
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    await migrationScope.ServiceProvider.GetRequiredService<BuildCvDbContext>().Database.MigrateAsync();
}

// First in the pipeline by design: every downstream decision that reads the peer address
// (rate-limit partitioning, audit logging) or the scheme (HTTPS redirection, HSTS) has to see the
// real client, not the proxy. Off unless Network:ForwardedHeaders names the proxies allowed to
// speak for their clients — an unrestricted UseForwardedHeaders lets any caller spoof its address
// and defeat rate limiting outright.
if (forwardedHeaders.Enabled)
    app.UseForwardedHeaders(ForwardedHeadersConfiguration.Build(forwardedHeaders));

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();

if (allowedOrigins.Length > 0)
    app.UseCors(CorsPolicies.Strict);

app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<CsrfGuardMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi().AllowAnonymous();

app.MapAuthEndpoints();
app.MapResumeEndpoints();
app.MapJobEndpoints();
app.MapOrganizationEndpoints();
app.MapScoringEndpoints();

app.Run();

public partial class Program;
