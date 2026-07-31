using BuildCv.Application.Common.Services;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.Interceptors;
using BuildCv.Infrastructure.Security.Encryption;
using BuildCv.Infrastructure.Tests.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

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

    public static BuildCvDbContext Create(
        string connectionString, TimeProvider timeProvider, ICurrentUser? currentUser = null)
    {
        var blindIndex = BlindIndex();

        var options = new DbContextOptionsBuilder<BuildCvDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(
                new AuditSaveChangesInterceptor(currentUser ?? new UnknownCurrentUser(), timeProvider),
                new BlindIndexSaveChangesInterceptor(
                    new AccountEmailIndex(blindIndex), new RefreshTokenIndex(blindIndex)))
            // Stated rather than merely omitted. Sensitive-data logging writes parameter values into
            // the log, and the parameters here include blind-index digests and freshly built
            // envelopes. It must be off in every environment, including this one.
            .EnableSensitiveDataLogging(false)
            .Options;

        return new BuildCvDbContext(options, Encryptor());
    }
}
