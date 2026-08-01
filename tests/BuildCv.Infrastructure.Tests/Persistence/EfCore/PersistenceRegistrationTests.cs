using BuildCv.Application.Common.Repositories;
using BuildCv.Infrastructure;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Persistence.EfCore;
using BuildCv.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// What AddInfrastructure actually composed, read back off the built provider rather than off the source
// it was written from. No database is involved — building DbContextOptions opens no connection — so these
// fail a pull request instead of a deployment.
public sealed class PersistenceRegistrationTests
{
    private const string ConnectionString =
        "Server=registration-only;Database=BuildCv;User Id=sa;Password=unused;TrustServerCertificate=True";

    // Requirement 1. Sensitive-data logging writes parameter values into the log, and the parameters on
    // this context are blind-index digests and freshly sealed envelopes — the material the encryption
    // exists to keep out of a dump. It was pinned only in the test host until now, because no production
    // AddDbContext existed. Asserting it against the COMPOSED provider is what stops a future
    // `if (environment.IsDevelopment())` branch from turning it back on unnoticed.
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void SqlServerProvider_NeverEnablesSensitiveDataLogging(string environmentName)
    {
        using var provider = BuildProvider(SqlServerSettings(), environmentName);

        CoreOptions(provider).IsSensitiveDataLoggingEnabled.Should().BeFalse();
    }

    // Requirement 2. The blind-index pass must run FIRST, so it only ever sees entity states the
    // application produced. The audit pass rewrites states — it turns a Deleted root into a Modified
    // tombstone — and running it first would hand an Account to the blind-index pass as Modified, sending
    // it back through Compute() under the ACTIVE key. That is harmless today only because reassigning an
    // equal byte[] is a no-op under EF's structural comparer: a property of EF, not of this code.
    [Fact]
    public void SqlServerProvider_RunsTheBlindIndexInterceptorBeforeTheAuditInterceptor()
    {
        using var provider = BuildProvider(SqlServerSettings(), "Production");

        var interceptors = CoreOptions(provider).Interceptors!.ToList();

        interceptors.OfType<ISaveChangesInterceptor>().Should().HaveCount(2);
        interceptors[0].Should().BeOfType<BlindIndexSaveChangesInterceptor>();
        interceptors[1].Should().BeOfType<AuditSaveChangesInterceptor>();
    }

    [Fact]
    public void SqlServerProvider_RegistersTheEfRepositoriesScoped()
    {
        using var provider = BuildProvider(SqlServerSettings(), "Production");
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAccountRepository>().Should().BeOfType<AccountRepository>();
        scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>().Should().BeOfType<RefreshTokenRepository>();
        scope.ServiceProvider.GetRequiredService<IResumeRepository>().Should().BeOfType<ResumeRepository>();
        scope.ServiceProvider.GetRequiredService<IJobPostingRepository>().Should().BeOfType<JobPostingRepository>();
        scope.ServiceProvider.GetRequiredService<IOrganizationRepository>().Should().BeOfType<OrganizationRepository>();
        scope.ServiceProvider.GetRequiredService<IAnalysisRepository>().Should().BeOfType<AnalysisRepository>();
    }

    // The default direction matters more than the default value: an unset key must not be able to hand a
    // deployed host a store that forgets everything on restart.
    [Fact]
    public void AnUnsetProvider_DefaultsToSqlServer()
    {
        var settings = SqlServerSettings();
        settings.Remove(PersistenceConfiguration.ProviderKey);

        using var provider = BuildProvider(settings, "Production");
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAccountRepository>().Should().BeOfType<AccountRepository>();
    }

    [Fact]
    public void TheInMemoryProvider_IsAllowedInDevelopment()
    {
        using var provider = BuildProvider(InMemorySettings(), "Development");

        provider.GetRequiredService<IAccountRepository>().Should().BeOfType<InMemoryAccountRepository>();
    }

    // Fails at registration, not at the first write. The only way to discover an in-memory store on a
    // deployed host at runtime is that everybody is logged out after every deploy.
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void TheInMemoryProvider_RefusesToRegisterOutsideDevelopment(string environmentName)
    {
        var act = () => BuildProvider(InMemorySettings(), environmentName);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*would discard all data on restart*");
    }

    // The escape hatch a test host uses when it deliberately builds production-shaped configuration.
    [Fact]
    public void TheInMemoryProvider_IsAllowedOutsideDevelopmentWhenExplicitlyAcknowledged()
    {
        var settings = InMemorySettings();
        settings[PersistenceConfiguration.AllowInMemoryOutsideDevelopmentKey] = "true";

        using var provider = BuildProvider(settings, "Staging");

        provider.GetRequiredService<IAccountRepository>().Should().BeOfType<InMemoryAccountRepository>();
    }

    [Fact]
    public void AnUnrecognizedProvider_FailsNamingTheSupportedValues()
    {
        var settings = SqlServerSettings();
        settings[PersistenceConfiguration.ProviderKey] = "Postgres";

        var act = () => BuildProvider(settings, "Production");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SqlServer*InMemory*");
    }

    // Requirement 8. ConnectionStrings:BuildCv is the application's setting and this is what reads it.
    // The local default comes from BuildCvDbContextFactory, the one committed copy of that string —
    // appsettings used to hold a second copy that nothing consumed and that was free to drift away from
    // the one `dotnet ef` uses.
    [Fact]
    public void TheConnectionString_ComesFromConfigurationWhenItIsSet()
    {
        using var provider = BuildProvider(SqlServerSettings(), "Production");

        SqlServerConnectionString(provider).Should().Contain("registration-only");
    }

    [Fact]
    public void TheConnectionString_FallsBackToTheDesignTimeDefaultLocally()
    {
        var settings = SqlServerSettings();
        settings.Remove($"ConnectionStrings:{PersistenceConfiguration.ConnectionStringName}");

        using var provider = BuildProvider(settings, "Development");

        SqlServerConnectionString(provider).Should().Be(BuildCvDbContextFactory.DefaultConnectionString);
    }

    // Pointing a deployed host at localhost silently is worse than refusing to start.
    [Fact]
    public void TheConnectionString_HasNoDefaultOutsideALocalComposition()
    {
        var settings = SqlServerSettings();
        settings.Remove($"ConnectionStrings:{PersistenceConfiguration.ConnectionStringName}");

        using var provider = BuildProvider(settings, "Production");

        var act = () => SqlServerConnectionString(provider);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:BuildCv must be configured*");
    }

    private static CoreOptionsExtension CoreOptions(IServiceProvider provider) =>
        provider.GetRequiredService<DbContextOptions<BuildCvDbContext>>().FindExtension<CoreOptionsExtension>()!;

    private static string? SqlServerConnectionString(IServiceProvider provider) =>
        provider.GetRequiredService<DbContextOptions<BuildCvDbContext>>()
            .Extensions.OfType<RelationalOptionsExtension>().Single().ConnectionString;

    private static Dictionary<string, string?> SqlServerSettings()
    {
        var settings = BaseSettings();
        settings[PersistenceConfiguration.ProviderKey] = PersistenceConfiguration.SqlServerProvider;
        settings[$"ConnectionStrings:{PersistenceConfiguration.ConnectionStringName}"] = ConnectionString;
        return settings;
    }

    private static Dictionary<string, string?> InMemorySettings()
    {
        var settings = BaseSettings();
        settings[PersistenceConfiguration.ProviderKey] = PersistenceConfiguration.InMemoryProvider;
        return settings;
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Jwt:SigningKey"] = "test-signing-key-min-32-characters-long-0123456789",
        ["Encryption:ActiveKeyId"] = "v1",
        ["Encryption:Keys:v1:Aes"] = "Z6h2YbISQC6Wo2Xbs2xQr1PistFWXwHrenrptzxtc6o=",
        ["Encryption:BlindIndex:ActiveKeyId"] = "b1",
        ["Encryption:BlindIndex:Keys:b1"] = "Xw273xuvdyoZuGb8kJo1vYXumxFtiHqIZkntZaZLegs="
    };

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings, string environmentName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection()
            .AddInfrastructure(configuration, environmentName)
            .BuildServiceProvider();
    }
}
