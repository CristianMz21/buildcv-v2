using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// Turns any model value that can project to text into an encrypted varbinary column.
//
// EF builds the conversion from expression trees, so the two lambdas stay trivial and capture the
// encryptor, the property path and the text projections as closure state. Anything more elaborate
// inside the expression would have to be translatable by EF, which it is not.
//
// `context` must be the fully-qualified property path (e.g. "Account.Email"). It is the AAD, so it
// also decides which column an envelope is allowed to decrypt in.
internal sealed class EncryptedConverter<T> : ValueConverter<T, byte[]>
{
    public EncryptedConverter(IFieldEncryptor encryptor, string context, Func<T, string> toText, Func<string, T> fromText)
        : base(
            value => encryptor.Encrypt(toText(value), context),
            envelope => fromText(encryptor.Decrypt(envelope, context)))
        => Context = context;

    public string Context { get; }
}
