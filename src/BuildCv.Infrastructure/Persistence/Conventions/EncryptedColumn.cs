using BuildCv.Infrastructure.Security.Encryption;

namespace BuildCv.Infrastructure.Persistence.Conventions;

// Column widths for encrypted varbinary columns, in total envelope bytes.
//
// A width is only pinned where the Domain itself bounds the plaintext; everything else gets
// varbinary(max) by omission. Guessing a cap for an unbounded Domain string would turn a legitimate
// long value into a truncation error at insert time — and truncating an AES-GCM envelope destroys
// the row, because the tag lives in the last 16 bytes.
//
// EncryptedColumnSizeTests keeps EnvelopeOverhead honest against what the encryptor actually emits.
internal static class EncryptedColumn
{
    // What the envelope adds on top of the UTF-8 plaintext, at the largest key id the ring accepts:
    // version byte + key-id-length byte + key id + 96-bit nonce + 128-bit GCM tag.
    public const int EnvelopeOverhead =
        AesGcmFieldEncryptor.HeaderSizeInBytes
        + KeyRingValidation.MaxKeyIdLength
        + AesGcmFieldEncryptor.NonceSizeInBytes
        + AesGcmFieldEncryptor.TagSizeInBytes;

    // Email: the Domain regex admits ASCII only and caps at 254 characters, so 254 plaintext bytes.
    public const int Email = 512;

    // RefreshToken.Token: 43..500 characters of base64url, so 500 plaintext bytes.
    public const int Token = 1024;

    // PersonName caps at 200 characters; 4 bytes each is the UTF-8 worst case.
    public const int PersonName = 1024;

    // OrganizationName caps at 150 characters.
    public const int OrganizationName = 768;

    // PhoneNumber caps at 20 characters and the regex admits '+' and digits only.
    public const int PhoneNumber = 128;

    // Deliberately no Technology entry. A skill name is analytical data and is stored as plaintext —
    // TechnologyConverter says so, and SchemaRoundTripTests proves it is queryable in SQL. A width
    // sitting in a type called EncryptedColumn for a column that is not encrypted is the same
    // misleading-name trap the EncryptedComparers -> ConvertedComparers rename existed to close.
}
