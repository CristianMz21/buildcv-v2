using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Security.Encryption;
using BuildCv.Infrastructure.Tests.Security.Encryption;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence.Converters;

// There is no DbContext yet, so the converters are exercised as the plain functions EF will call.
public class EncryptedConverterTests
{
    private const string Context = "Account.Email";

    private readonly IFieldEncryptor _encryptor = new AesGcmFieldEncryptor(EncryptionTestKeys.SingleKeyRing());

    private EncryptedConverter<Email> EmailConverter() =>
        new(_encryptor, Context, email => email.Value, Email.Create);

    [Fact]
    public void ConvertFromProvider_ValueWrittenByConvertToProvider_RebuildsTheValueObject()
    {
        var converter = EmailConverter();
        var email = Email.Create("candidate@example.com");

        var stored = converter.ConvertToProvider(email);
        var loaded = converter.ConvertFromProvider(stored);

        loaded.Should().BeOfType<Email>().Which.Value.Should().Be("candidate@example.com");
    }

    [Fact]
    public void ConvertToProvider_ProducesOpaqueBytes()
    {
        var converter = EmailConverter();

        var stored = (byte[])converter.ConvertToProvider(Email.Create("candidate@example.com"))!;

        System.Text.Encoding.UTF8.GetString(stored).Should().NotContain("candidate@example.com");
    }

    [Fact]
    public void ConvertToProvider_SameValueTwice_ProducesDifferentBytes()
    {
        var converter = EmailConverter();
        var email = Email.Create("candidate@example.com");

        var first = (byte[])converter.ConvertToProvider(email)!;
        var second = (byte[])converter.ConvertToProvider(email)!;

        first.Should().NotEqual(second, "the column must not leak equality between rows");
    }

    [Fact]
    public void ConvertFromProvider_BytesWrittenForAnotherColumn_Throws()
    {
        // Copying Resume.ContactInformation.Summary bytes into Account.Email must fail the
        // authentication tag, not silently swap one confidential value for another.
        var summaryConverter = new EncryptedConverter<string>(
            _encryptor, "Resume.ContactInformation.Summary", value => value, value => value);
        var foreignBytes = summaryConverter.ConvertToProvider("candidate@example.com");

        var act = () => EmailConverter().ConvertFromProvider(foreignBytes);

        act.Should().Throw<FieldDecryptionException>()
            .Which.Context.Should().Be(Context);
    }

    [Fact]
    public void Context_ExposesThePropertyPathTheColumnIsBoundTo()
    {
        EmailConverter().Context.Should().Be(Context);
    }

    [Fact]
    public void ConvertFromProvider_EncryptedJsonList_RoundTripsThroughBothLayers()
    {
        // Encrypted list columns compose the JSON codec with the envelope; this is the shape the
        // resume mappings will use for Highlights and Keywords.
        var converter = new EncryptedConverter<IReadOnlyList<string>>(
            _encryptor,
            "Resume.Experience.Highlights",
            JsonListCodec.ToJson,
            JsonListCodec.ToStringList);
        IReadOnlyList<string> highlights = ["Led the migration", "Cut p99 latency by 40%"];

        var stored = converter.ConvertToProvider(highlights);
        var loaded = converter.ConvertFromProvider(stored);

        loaded.Should().BeAssignableTo<IReadOnlyList<string>>().Which.Should().Equal(highlights);
    }

    [Fact]
    public void ConvertFromProvider_EmptyString_RoundTrips()
    {
        var converter = new EncryptedConverter<string>(_encryptor, "Resume.ContactInformation.Summary", v => v, v => v);

        var stored = converter.ConvertToProvider(string.Empty);

        converter.ConvertFromProvider(stored).Should().Be(string.Empty);
    }
}
