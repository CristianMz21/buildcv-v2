using System.Text;
using System.Threading.RateLimiting;
using BuildCv.Api.Common;
using BuildCv.Api.Endpoints;
using BuildCv.Api.Health;
using BuildCv.Api.Observability;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Observability;
using BuildCv.Application.Common.Services;
using BuildCv.Infrastructure;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
builder.Services.AddSingleton<ResumeImportRateLimiter>();
builder.Services.AddSingleton<DocumentExtractionRateLimiter>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddJwtBearer(AuthenticationSchemes.ExpiredAccessTokenAllowed);

// Bearer validation is configured through the options system so it resolves the very same
// JwtSettings instance that TokenService signs with. Reading configuration eagerly off the
// builder would capture values from the sources known at that moment and silently miss any
// provider added while the host is being built, desynchronizing the validation key from the
// signing key and rejecting every token issued by this API.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtSettings>>((options, jwtOptions) =>
        JwtBearerConfiguration.Configure(options, jwtOptions.Value, validateLifetime: true));

// Reachable only through an explicit AuthenticateAsync call in /auth/logout — it is not the
// default scheme and no authorization policy names it, so no protected endpoint can be entered
// with an expired token. See AuthenticationSchemes.ExpiredAccessTokenAllowed for the rationale.
builder.Services.AddOptions<JwtBearerOptions>(AuthenticationSchemes.ExpiredAccessTokenAllowed)
    .Configure<IOptions<JwtSettings>>((options, jwtOptions) =>
        JwtBearerConfiguration.Configure(options, jwtOptions.Value, validateLifetime: false));

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

        // Counted from the ENDPOINT's policy metadata, because that is the only attribution this
        // callback can make: OnRejectedContext carries exactly two things, HttpContext and the failed
        // Lease, and neither names the limiter that refused.
        // So the tag is exact for every route without a policy (there is only the global limiter to
        // refuse it) and, on the four routes that have one, reports that policy even in the rare case
        // where the global 100/min ceiling was hit first — reachable only by spending the global budget
        // elsewhere and then arriving here, since the named windows are 5/min and 20/min. Stated rather
        // than papered over; both directions are pinned by ThrottleMetricsTests.
        var policy = context.HttpContext.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName
            ?? ThrottlePolicies.Global;
        context.HttpContext.RequestServices.GetRequiredService<BuildCvMetrics>().ThrottleRejection(policy);

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
    // /auth/logout is anonymous and state-changing, and each authenticated hit scans the token
    // store, so it needs its own ceiling — separate from the brute-force window because it tests
    // no secret and a logout button must not compete with logins for the same 5/min budget.
    options.AddPolicy(RateLimitPolicies.Logout, httpContext => RateLimitPartition.GetFixedWindowLimiter(
        RateLimitPartitions.ClientKey(httpContext),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
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
// ON IN EVERY ENVIRONMENT, not at its default of IsDevelopment(). Left alone, a body that is not valid
// JSON answers an EMPTY 400 in production and a logged 500 in Development — one input, two wrong
// answers, neither ProblemDetails-shaped. Turning it on everywhere makes binding failures reach
// MalformedRequestExceptionHandler, which is the only way this API can answer them in its own shape.
// Binding failures are logged at Debug by the framework, so this does not turn malformed input into
// error-level noise.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddExceptionHandler<MalformedRequestExceptionHandler>();
// Before the catch-all: a storage conflict is a 409 the client can act on, and letting it fall through
// to the 500 handler would tell a caller whose only problem is a stale copy that the server is broken.
builder.Services.AddExceptionHandler<PersistenceExceptionHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// The store probe is the only readiness check, and it is TAGGED rather than merely registered:
// /health/live selects no checks at all and /health/ready selects this tag, so neither endpoint means
// "whatever happens to be registered". See HealthEndpoints for why the two probes must differ.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>(
        DatabaseHealthCheck.Name,
        failureStatus: HealthStatus.Unhealthy,
        tags: [DatabaseHealthCheck.ReadinessTag]);

// The transformer is the only configuration here, and it exists because the default document states a
// union this API cannot produce; see FiniteNumberSchemaTransformer.
builder.Services.AddOpenApi(options => options.AddSchemaTransformer<FiniteNumberSchemaTransformer>());

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

// Before the exception handler, so the 500 a caller is shown and the stack trace this process logged
// carry the same id. See CorrelationIdMiddleware for why the echo is written from OnStarting.
app.UseMiddleware<CorrelationIdMiddleware>();

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

// Mapped before the /v1 group and deliberately not inside it: probe URLs live in deployment manifests,
// not in client libraries, so they must not move when the product contract versions.
app.MapHealthEndpoints();

// Every public route lives under one URL version segment, so the analysis contract can change in a
// /v2 without breaking whatever clients exist by then. A hand-rolled group rather than the
// Asp.Versioning package, which is not in the dependency graph: with exactly one version and no
// header or media-type negotiation, the package would buy a version-set model this API does not use
// at the price of a dependency and a second routing concept. One group is the whole mechanism.
//
// The version prefix is route surface, not just decoration — three path-matched pieces move with it
// and break silently if they drift: CsrfGuardMiddleware.ExemptPaths matches on path,
// AuthCookies.RefreshCookiePath scopes the refresh cookie to its endpoint, and the Location headers
// built in the endpoint lambdas name absolute paths.
var v1 = app.MapGroup("/v1");

v1.MapAuthEndpoints();
v1.MapResumeEndpoints();
v1.MapJobEndpoints();
v1.MapJobOfferEndpoints();
v1.MapOrganizationEndpoints();
v1.MapScoringEndpoints();

app.Run();

public partial class Program;
