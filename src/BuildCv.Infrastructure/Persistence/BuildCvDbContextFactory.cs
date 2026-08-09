using System.Security.Cryptography;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BuildCv.Infrastructure.Persistence;

// Builds a context for `dotnet ef migrations add` and `dotnet ef database update`.
//
// The key ring here is a throwaway. Migrations only need the model's SHAPE — table and column
// definitions — and the shape of an encrypted column is varbinary regardless of which key seals it.
// No value is ever encrypted or decrypted during scaffolding, so these bytes never touch a row.
//
// Both rings are supplied, and that is not optional: AddInfrastructure validates Encryption:Keys AND
// Encryption:BlindIndex:Keys under ValidateOnStart, and BlindIndexKeyRing rejects a ring that is
// empty or missing its active id. A factory that seeded only the AES ring would fail validation
// before EF ever got to build the model, and the error would talk about configuration rather than
// about migrations.
public sealed class BuildCvDbContextFactory : IDesignTimeDbContextFactory<BuildCvDbContext>
{
    // Read by `dotnet ef` so a developer can scaffold against their own instance without editing
    // this file. It is deliberately its own variable rather than ConnectionStrings__BuildCv: pointing
    // the migration tooling somewhere is a different decision from pointing the running application
    // somewhere, and conflating them is how a migration gets applied to the wrong database.
    public const string ConnectionStringVariable = "BUILDCV_MIGRATIONS_CONNECTION";

    // Matches docker-compose.yml, so `docker compose up -d` followed by `dotnet ef database update`
    // works with no further setup.
    public const string DefaultConnectionString =
        "Server=localhost,1433;Database=BuildCv;User Id=sa;Password=Local_Dev_Password_1;" +
        "TrustServerCertificate=True;Encrypt=False";

    private const string DesignTimeKeyId = "design-time";

    public BuildCvDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } configured
                ? configured
                : DefaultConnectionString;

        var options = new DbContextOptionsBuilder<BuildCvDbContext>()
            .UseSqlServer(connectionString, sqlServer =>
            {
                sqlServer.MigrationsHistoryTable("__EFMigrationsHistory", "dbo");

                // The same retry policy AddPersistence gives the running application, for a reason
                // that is specific to this factory rather than symmetry. A serverless Azure SQL
                // database auto-pauses when idle, and its first connection afterwards fails with
                // error 40613 while it resumes — roughly a minute. `dotnet ef database update` is
                // exactly the caller that meets a paused database: it runs on a deploy, and a
                // deploy outside working hours is when the database is most likely to be asleep.
                // Without this the migration step fails intermittently, and only on the deploys
                // nobody is watching.
                sqlServer.EnableRetryOnFailure();
            })
            .Options;

        return new BuildCvDbContext(options, new AesGcmFieldEncryptor(new EncryptionKeyRing(DesignTimeSettings())));
    }

    // Both sections, populated. The AES ring is the only one the factory itself consumes, but the
    // blind-index section is filled in so this object satisfies the SAME validation AddInfrastructure
    // runs — DesignTimeSettingsTests asserts both rings accept it, which is what keeps
    // `dotnet ef migrations add` working the next time startup validation gets stricter.
    //
    // Secrets are derived rather than written as literal base64, so nothing that looks like a
    // production key is committed and no secret scanner has to decide whether it is one.
    internal static EncryptionSettings DesignTimeSettings() =>
        new()
        {
            ActiveKeyId = DesignTimeKeyId,
            Keys = { [DesignTimeKeyId] = new EncryptionKeyMaterial { Aes = DesignTimeSecret("aes") } },
            BlindIndex = new BlindIndexSettings
            {
                ActiveKeyId = DesignTimeKeyId,
                Keys = { [DesignTimeKeyId] = DesignTimeSecret("blind-index") }
            }
        };

    // SHA-256 is exactly the 32 bytes both rings demand.
    private static string DesignTimeSecret(string label) =>
        Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"buildcv-design-time:{label}")));
}
