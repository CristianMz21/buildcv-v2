using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildCv.Infrastructure.Persistence.Conventions;

// One entry point for every encrypted column, so three things can never be forgotten independently:
// the converter, the model-value comparer, and the annotations the guardrail tests read.
//
// Dropping the comparer is the dangerous omission. EF's default comparer for a converted property
// compares PROVIDER values, and an encrypted provider value is a fresh random-nonce envelope every
// time it is produced — every tracked entity would look modified on every SaveChanges, rewriting
// every encrypted column of every loaded row. It would work, quietly, and be enormously expensive.
//
// The overloads take the non-generic PropertyBuilder deliberately: it is the common base of
// PropertyBuilder<T>, so a nullable reference member (PhoneNumber?) and a non-nullable one bind the
// same way. EF never invokes a converter for a null model value, so the nullable case needs no
// special handling beyond IsRequired(false), which the configuration states on its own.
internal static class EncryptedPropertyBuilderExtensions
{
    public static PropertyBuilder IsEncrypted<T>(
        this PropertyBuilder builder,
        IFieldEncryptor encryptor,
        string context,
        Func<T, string> toText,
        Func<string, T> fromText,
        ValueComparer<T> comparer,
        int? maxLength = null)
        where T : class
    {
        var converter = new EncryptedConverter<T>(encryptor, context, toText, fromText);
        builder.HasConversion(converter, comparer);

        // Omitted length means varbinary(max): the Domain does not bound this plaintext, so neither
        // does the column.
        if (maxLength is { } length)
            builder.HasMaxLength(length);

        builder.HasAnnotation(PersistenceAnnotations.Encrypted, true);
        builder.HasAnnotation(PersistenceAnnotations.EncryptionContext, converter.Context);
        return builder;
    }

    public static PropertyBuilder IsEncryptedText(
        this PropertyBuilder builder, IFieldEncryptor encryptor, string context, int? maxLength = null) =>
        builder.IsEncrypted(
            encryptor, context, text => text, text => text, ConvertedComparers.ForValueObject<string>(), maxLength);

    public static PropertyBuilder IsEncryptedEmail(
        this PropertyBuilder builder, IFieldEncryptor encryptor, string context) =>
        builder.IsEncrypted(
            encryptor,
            context,
            email => email.Value,
            Email.Create,
            ConvertedComparers.ForValueObject<Email>(),
            EncryptedColumn.Email);

    public static PropertyBuilder IsEncryptedPersonName(
        this PropertyBuilder builder, IFieldEncryptor encryptor, string context) =>
        builder.IsEncrypted(
            encryptor,
            context,
            name => name.Value,
            PersonName.Create,
            ConvertedComparers.ForValueObject<PersonName>(),
            EncryptedColumn.PersonName);

    public static PropertyBuilder IsEncryptedOrganizationName(
        this PropertyBuilder builder, IFieldEncryptor encryptor, string context) =>
        builder.IsEncrypted(
            encryptor,
            context,
            name => name.Value,
            OrganizationName.Create,
            ConvertedComparers.ForValueObject<OrganizationName>(),
            EncryptedColumn.OrganizationName);

    public static PropertyBuilder IsEncryptedPhoneNumber(
        this PropertyBuilder builder, IFieldEncryptor encryptor, string context) =>
        builder.IsEncrypted(
            encryptor,
            context,
            phoneNumber => phoneNumber.Value,
            PhoneNumber.Create,
            ConvertedComparers.ForValueObject<PhoneNumber>(),
            EncryptedColumn.PhoneNumber);

    // Url is revalidated through its factory on the way back, so a stored value that no longer
    // parses fails loudly instead of materializing an aggregate the Domain would have rejected.
    public static PropertyBuilder IsEncryptedUrl(
        this PropertyBuilder builder, IFieldEncryptor encryptor, string context) =>
        builder.IsEncrypted(
            encryptor,
            context,
            url => url.Value,
            Url.Create,
            ConvertedComparers.ForValueObject<Url>(),
            maxLength: null);

    // JSON first, then the envelope: the list is serialized to text and the text is what gets
    // sealed, so the column holds one opaque blob rather than a readable array of ciphertexts.
    public static PropertyBuilder IsEncryptedStringList(
        this PropertyBuilder builder, IFieldEncryptor encryptor, string context) =>
        builder.IsEncrypted<IReadOnlyList<string>>(
            encryptor,
            context,
            values => JsonListCodec.ToJson(values),
            json => JsonListCodec.ToStringList(json),
            ConvertedComparers.ForList<string>());

    public static PropertyBuilder IsEncryptedProfileList(
        this PropertyBuilder builder, IFieldEncryptor encryptor, string context) =>
        builder.IsEncrypted<IReadOnlyList<Profile>>(
            encryptor,
            context,
            values => JsonListCodec.ToJson(values),
            json => JsonListCodec.ToProfileList(json),
            ConvertedComparers.ForList<Profile>());
}
