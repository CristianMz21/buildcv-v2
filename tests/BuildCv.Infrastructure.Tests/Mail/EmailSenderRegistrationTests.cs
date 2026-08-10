using BuildCv.Application.Common.Services;
using BuildCv.Infrastructure;
using BuildCv.Infrastructure.Mail;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BuildCv.Infrastructure.Tests.Mail;

// Which IEmailSender a host gets, and what decides it.
//
// The host is the whole switch, deliberately: there is no Enabled flag beside it, because two settings
// that can disagree about one fact is how a deployment ends up configured to send through a host it was
// told to ignore. These tests pin that the switch is the host and nothing else.
public class EmailSenderRegistrationTests
{
    private const string Aes = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string BlindIndexKey = "HxwdHhsaGRgXFhUUExIREA8ODQwLCgkIBwYFBAMCAQA=";

    // The shipped default. Nothing is configured, so nothing is sent -- and the sender says so rather
    // than dropping the message, which is what makes /v1/auth/password-reset answer 503 instead of
    // telling somebody to watch an inbox that will never receive anything.
    [Fact]
    public void AddInfrastructure_WithNoSmtpHost_ResolvesTheSenderThatRefuses()
    {
        using var provider = BuildProvider(Complete());

        var sender = provider.GetRequiredService<IEmailSender>();

        sender.Should().BeOfType<UnconfiguredEmailSender>();
        sender.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void AddInfrastructure_WithAnSmtpHost_ResolvesTheSenderThatSends()
    {
        var settings = Complete();
        settings["Email:Smtp:Host"] = "smtp.example.com";
        settings["Email:Smtp:FromAddress"] = "no-reply@example.com";

        using var provider = BuildProvider(settings);

        var sender = provider.GetRequiredService<IEmailSender>();

        sender.Should().BeOfType<SmtpEmailSender>();
        sender.IsConfigured.Should().BeTrue();
    }

    // A host with nowhere to send FROM is a configuration nobody can act on: SPF and DKIM are checked
    // against that address, so a message without one is refused by the receiving side rather than by
    // this code. Caught at startup because the send path deliberately swallows its own failure -- to
    // avoid leaking whether an address is registered -- so a half-configured host would otherwise stay
    // invisible until somebody noticed nobody was receiving mail.
    [Fact]
    public void AddInfrastructure_WithAnSmtpHostAndNoFromAddress_FailsWhenTheOptionsAreRealized()
    {
        var settings = Complete();
        settings["Email:Smtp:Host"] = "smtp.example.com";

        using var provider = BuildProvider(settings);

        var realize = () => provider.GetRequiredService<IOptions<SmtpSettings>>().Value;

        realize.Should().Throw<OptionsValidationException>()
            .WithMessage("*FromAddress*");
    }

    // The default port has to be a port. A typo that lands outside the range would otherwise surface as
    // a connection failure at send time, on the path that reports nothing.
    [Fact]
    public void AddInfrastructure_WithAnImpossiblePort_FailsWhenTheOptionsAreRealized()
    {
        var settings = Complete();
        settings["Email:Smtp:Host"] = "smtp.example.com";
        settings["Email:Smtp:FromAddress"] = "no-reply@example.com";
        settings["Email:Smtp:Port"] = "70000";

        using var provider = BuildProvider(settings);

        var realize = () => provider.GetRequiredService<IOptions<SmtpSettings>>().Value;

        realize.Should().Throw<OptionsValidationException>().WithMessage("*Port*");
    }

    // Whitespace is not a host. Without this, a value left as " " in a deployment template would select
    // the sending path and then fail on every message, on the branch that reports nothing.
    [Fact]
    public void AddInfrastructure_WithABlankSmtpHost_StillResolvesTheSenderThatRefuses()
    {
        var settings = Complete();
        settings["Email:Smtp:Host"] = "   ";

        using var provider = BuildProvider(settings);

        provider.GetRequiredService<IEmailSender>().Should().BeOfType<UnconfiguredEmailSender>();
    }

    private static Dictionary<string, string?> Complete() => new()
    {
        ["Jwt:SigningKey"] = "test-signing-key-min-32-characters-long-0123456789",
        ["Encryption:ActiveKeyId"] = "v1",
        ["Encryption:Keys:v1:Aes"] = Aes,
        ["Encryption:BlindIndex:ActiveKeyId"] = "b1",
        ["Encryption:BlindIndex:Keys:b1"] = BlindIndexKey
    };

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        // AddLogging, which AddInfrastructure deliberately does not do: logging belongs to the host, and
        // both senders take an ILogger. A real composition has it; a bare ServiceCollection does not.
        return new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(configuration, "Development")
            .BuildServiceProvider();
    }
}
