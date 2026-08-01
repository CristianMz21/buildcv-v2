using BuildCv.Infrastructure.Security.Encryption;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BuildCv.Infrastructure.Tests.Security.Encryption;

public class EncryptionRegistrationTests
{
    private const string Aes = "Z6h2YbISQC6Wo2Xbs2xQr1PistFWXwHrenrptzxtc6o=";
    private const string BlindIndexKey = "Xw273xuvdyoZuGb8kJo1vYXumxFtiHqIZkntZaZLegs=";

    [Fact]
    public void AddInfrastructure_WithConfiguredKeys_ResolvesASingleFieldEncryptorAndBlindIndex()
    {
        using var provider = BuildProvider(Complete());

        var encryptor = provider.GetRequiredService<IFieldEncryptor>();
        var blindIndex = provider.GetRequiredService<IBlindIndex>();

        encryptor.Should().BeSameAs(provider.GetRequiredService<IFieldEncryptor>());
        blindIndex.Should().BeSameAs(provider.GetRequiredService<IBlindIndex>());
        encryptor.Decrypt(encryptor.Encrypt("candidate@example.com", "Account.Email"), "Account.Email")
            .Should().Be("candidate@example.com");
        blindIndex.Compute("candidate@example.com", "Account.Email")
            .Should().HaveCount(HmacBlindIndex.DigestSizeInBytes);
    }

    [Fact]
    public void AddInfrastructure_BindsTheTwoRotationPointersIndependently()
    {
        var settings = Complete();
        settings["Encryption:ActiveKeyId"] = "v2";
        settings["Encryption:Keys:v2:Aes"] = "SUcLJu1U3OAVv8dS5Hnm+WVpjSi4jEiFsNgIMr6B+wI=";

        using var provider = BuildProvider(settings);

        provider.GetRequiredService<EncryptionKeyRing>().ActiveKeyId.Should().Be("v2");
        provider.GetRequiredService<BlindIndexKeyRing>().ActiveKeyId.Should().Be("b1");
    }

    [Fact]
    public void AddInfrastructure_WithoutAnEncryptionSection_FailsWhenTheOptionsAreRealized()
    {
        // ValidateOnStart turns this into a startup failure for the Api; here the same validator runs
        // the moment the key ring is built.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key-min-32-characters-long-0123456789"
        });

        var act = () => provider.GetRequiredService<IFieldEncryptor>();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Encryption:Keys must contain at least one key*");
    }

    [Fact]
    public void AddInfrastructure_WithoutABlindIndexSection_FailsWhenTheOptionsAreRealized()
    {
        // A host that can encrypt but cannot compute a lookup digest is just as unusable, and far
        // harder to diagnose once it is already serving traffic.
        var settings = Complete();
        settings.Remove("Encryption:BlindIndex:ActiveKeyId");
        settings.Remove("Encryption:BlindIndex:Keys:b1");

        using var provider = BuildProvider(settings);

        var act = () => provider.GetRequiredService<IFieldEncryptor>();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Encryption:BlindIndex:Keys must contain at least one key*");
    }

    [Fact]
    public void AddInfrastructure_WithMalformedKeyMaterial_FailsNamingTheOffendingKey()
    {
        var settings = Complete();
        settings["Encryption:Keys:v1:Aes"] = "not-base64!!";

        using var provider = BuildProvider(settings);

        var act = () => provider.GetRequiredService<IFieldEncryptor>();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Encryption:Keys:v1:Aes*");
    }

    private static Dictionary<string, string?> Complete() => new()
    {
        ["Jwt:SigningKey"] = "test-signing-key-min-32-characters-long-0123456789",
        ["Encryption:ActiveKeyId"] = "v1",
        ["Encryption:Keys:v1:Aes"] = Aes,
        ["Encryption:BlindIndex:ActiveKeyId"] = "b1",
        ["Encryption:BlindIndex:Keys:b1"] = BlindIndexKey
    };

    // AddInfrastructure requires an environment name — it decides whether the in-memory store may be
    // registered and whether the local connection string may be used, so it has no safe default. These
    // tests are about the key rings, not persistence; Development is the honest answer for a composition
    // that never resolves a DbContext.
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddInfrastructure(configuration, "Development").BuildServiceProvider();
    }
}
