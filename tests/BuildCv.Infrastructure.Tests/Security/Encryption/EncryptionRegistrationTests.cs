using BuildCv.Infrastructure.Security.Encryption;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BuildCv.Infrastructure.Tests.Security.Encryption;

public class EncryptionRegistrationTests
{
    private const string Aes = "Z6h2YbISQC6Wo2Xbs2xQr1PistFWXwHrenrptzxtc6o=";
    private const string Hmac = "plvayg6COUHk/ZPifZ4984Ps7ytigDoGldjSe+SVKsA=";

    [Fact]
    public void AddInfrastructure_WithConfiguredKeys_ResolvesASingleFieldEncryptorAndBlindIndex()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key-min-32-characters-long-0123456789",
            ["Encryption:ActiveKeyId"] = "v1",
            ["Encryption:Keys:v1:Aes"] = Aes,
            ["Encryption:Keys:v1:Hmac"] = Hmac
        });

        var encryptor = provider.GetRequiredService<IFieldEncryptor>();
        var blindIndex = provider.GetRequiredService<IBlindIndex>();

        encryptor.Should().BeSameAs(provider.GetRequiredService<IFieldEncryptor>());
        blindIndex.Should().BeSameAs(provider.GetRequiredService<IBlindIndex>());
        encryptor.Decrypt(encryptor.Encrypt("candidate@example.com", "Account.Email"), "Account.Email")
            .Should().Be("candidate@example.com");
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
    public void AddInfrastructure_WithMalformedKeyMaterial_FailsNamingTheOffendingKey()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key-min-32-characters-long-0123456789",
            ["Encryption:ActiveKeyId"] = "v1",
            ["Encryption:Keys:v1:Aes"] = "not-base64!!",
            ["Encryption:Keys:v1:Hmac"] = Hmac
        });

        var act = () => provider.GetRequiredService<IFieldEncryptor>();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Encryption:Keys:v1:Aes*");
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddInfrastructure(configuration).BuildServiceProvider();
    }
}
