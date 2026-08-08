using BuildCv.Application.Common.Services;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.Interceptors;
using BuildCv.Infrastructure.Security.Encryption;
using BuildCv.Infrastructure.Tests.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace BuildCv.Infrastructure.Tests.Persistence;

// Builds contexts the way the application does, with the real primitives. There is no mock encryptor
// here on purpose: a fake that returned its input would make every round-trip test pass while proving
// nothing about the envelope, the AAD binding or the column widths.
internal static class PersistenceTestContext
{
    // Never connects. Building a model does not open a connection, so the model-shape tests need no
    // database and stay out of the Integration category.
    private const string UnusedConnectionString =
        "Server=model-only;Database=BuildCv;User Id=sa;Password=unused;TrustServerCertificate=True";

    public static IFieldEncryptor Encryptor() => new AesGcmFieldEncryptor(EncryptionTestKeys.SingleKeyRing());

    public static IBlindIndex BlindIndex() => new HmacBlindIndex(EncryptionTestKeys.SingleBlindIndexRing());

    public static BuildCvDbContext ModelOnly() => Create(UnusedConnectionString, TimeProvider.System);

    // Provider-specific annotations — IsClustered, index filters, column types — are stripped from
    // the read-optimized runtime model EF hands back from DbContext.Model. Anything asserting on the
    // physical shape has to ask for the design-time model, which is the same one the migration
    // scaffolder reads.
    public static IModel DesignTimeModel(BuildCvDbContext context) =>
        context.GetService<IDesignTimeModel>().Model;

    // blindIndex is a parameter so a test can hand in a ROTATED key ring — a ring whose active key is
    // new and which still carries the retired one. That is the only way to reproduce the window in which
    // a lookup built on Compute() rather than ComputeCandidates() stops finding existing rows.
    //
    // trackingBehavior defaults to TrackAll, which is EF's default and what the model-shape and
    // round-trip tests were written against. The repository tests pass NoTracking, because that is what
    // AddInfrastructure configures and the repositories' explicit AsTracking() calls are only meaningful
    // against it.
    //
    // loggerFactory is null everywhere except EfCoreObservabilityLeakTests, which hands in a recorder so
    // that EVERYTHING EF Core writes while talking to a real SQL Server — command text, parameter list,
    // query compilation and the exception chain behind a failed SaveChanges — can be searched for
    // candidate content. That surface exists only on this provider, so it is only reachable from here.
    public static BuildCvDbContext Create(
        string connectionString,
        TimeProvider timeProvider,
        ICurrentUser? currentUser = null,
        IBlindIndex? blindIndex = null,
        QueryTrackingBehavior trackingBehavior = QueryTrackingBehavior.TrackAll,
        ILoggerFactory? loggerFactory = null)
    {
        var index = blindIndex ?? BlindIndex();

        var builder = new DbContextOptionsBuilder<BuildCvDbContext>()
            .UseSqlServer(connectionString)
            .UseQueryTrackingBehavior(trackingBehavior)
            // Same order AddInfrastructure pins, for the same reason: the blind-index pass must not
            // observe an entity state the audit pass rewrote.
            .AddInterceptors(
                new BlindIndexSaveChangesInterceptor(
                    new AccountEmailIndex(index), new RefreshTokenIndex(index)),
                new AuditSaveChangesInterceptor(currentUser ?? new UnknownCurrentUser(), timeProvider))
            // Stated rather than merely omitted. Sensitive-data logging writes parameter values into
            // the log, and the parameters here include blind-index digests and freshly built
            // envelopes. It must be off in every environment, including this one.
            //
            // It is also the negative control for EfCoreObservabilityLeakTests: flipping this one
            // argument to true is what proves that test can fail, and
            // TheTestHostNeverEnablesSensitiveDataLogging reads it back so the flip cannot be left
            // behind.
            .EnableSensitiveDataLogging(false);

        if (loggerFactory is not null)
        {
            builder.UseLoggerFactory(loggerFactory);

            // Not a performance knob — it is what makes the query-compilation log OBSERVABLE.
            //
            // EF caches its internal service provider, and the compiled-query cache lives inside it.
            // Measured: run EfCoreObservabilityLeakTests alone and every query compiles, so
            // Microsoft.EntityFrameworkCore.Query is captured; run it after the other integration
            // tests in the same collection and the same queries are already compiled, so that category
            // never appears and the surface goes unwatched depending on test order. A private internal
            // provider gives this context an empty query cache every time, which is the only way an
            // absence assertion over the compiled query EXPRESSION means the same thing on every run.
            builder.EnableServiceProviderCaching(false);
        }

        return new BuildCvDbContext(builder.Options, Encryptor());
    }
}
