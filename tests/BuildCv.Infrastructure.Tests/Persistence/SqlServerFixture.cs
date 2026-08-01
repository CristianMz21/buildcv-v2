using BuildCv.Application.Common.Services;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace BuildCv.Infrastructure.Tests.Persistence;

// One throwaway SQL Server for the whole integration run, migrated once.
//
// A real engine rather than the in-memory provider, because everything worth asserting here only
// exists in SQL Server: rowversion, filtered unique indexes, IDENTITY, varbinary widths and the
// migration itself. The in-memory provider would report success on a schema that cannot be created.
//
// The container is disposable and randomly named, so a run never touches the docker-compose instance
// a developer has data in.
public sealed class SqlServerFixture : IAsyncLifetime
{
    // Pinned rather than floating on 2022-latest: that tag gets repointed, and one such repoint moved
    // sqlcmd out of /opt/mssql-tools/bin, breaking the health-check wait strategy for everyone at once.
    // Keep this in step with the image in docker-compose.yml.
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04").Build();

    public string ConnectionString => _container.GetConnectionString();

    // Applies the committed migration rather than EnsureCreated. EnsureCreated builds the schema from
    // the model, which would let the model and the migration drift apart while every test still
    // passed — and the migration is what actually runs in production.
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    // A FRESH context every call. Reading back through the same instance would be answered from the
    // identity map, which proves nothing about what reached the database.
    public BuildCvDbContext NewContext(ICurrentUser? currentUser = null) =>
        PersistenceTestContext.Create(ConnectionString, TimeProvider.System, currentUser);

    // Shaped exactly like the one AddInfrastructure registers: NoTracking by default. The repository
    // tests use this one so the AsTracking() calls inside the repositories are the thing under test
    // rather than a courtesy on top of an ambient default that would have tracked everything anyway.
    public BuildCvDbContext NewApplicationContext(
        ICurrentUser? currentUser = null, IBlindIndex? blindIndex = null) =>
        PersistenceTestContext.Create(
            ConnectionString, TimeProvider.System, currentUser, blindIndex, QueryTrackingBehavior.NoTracking);
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
