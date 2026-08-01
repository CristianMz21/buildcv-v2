using Microsoft.Extensions.Configuration;

namespace BuildCv.Infrastructure.Persistence;

// The persistence knobs, named once.
//
// Public because the Api reads two of them as well — the auto-migrate step has to know it is running on
// SQL Server before it asks for a DbContext. Two copies of the string "Persistence:Provider" that can
// disagree is exactly how a host ends up migrating a database it is not going to use.
public static class PersistenceConfiguration
{
    public const string SectionName = "Persistence";

    public const string ProviderKey = $"{SectionName}:Provider";
    public const string AutoMigrateKey = $"{SectionName}:AutoMigrate";

    // The escape hatch for a host that is neither Development nor production: an integration test host
    // that deliberately builds the Staging-shaped configuration still needs the in-memory store. Named
    // at this length on purpose — nobody sets it by accident, and it greps.
    public const string AllowInMemoryOutsideDevelopmentKey = $"{SectionName}:AllowInMemoryOutsideDevelopment";

    // ConnectionStrings:BuildCv. The application's pointer at its database; the migration tooling has its
    // own (BuildCvDbContextFactory.ConnectionStringVariable) because aiming a migration and aiming a
    // running host are different decisions.
    public const string ConnectionStringName = "BuildCv";

    public const string SqlServerProvider = "SqlServer";
    public const string InMemoryProvider = "InMemory";

    // Defaults to SQL Server, and that direction matters: an unset key must not be able to hand a
    // deployed host a store that forgets everything on restart. Choosing InMemory has to be written down.
    public static string ResolveProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration[ProviderKey] is { Length: > 0 } provider ? provider : SqlServerProvider;
    }

    public static bool UsesSqlServer(IConfiguration configuration) =>
        string.Equals(ResolveProvider(configuration), SqlServerProvider, StringComparison.OrdinalIgnoreCase);

    public static bool AutoMigrateEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue(AutoMigrateKey, false);
    }
}
