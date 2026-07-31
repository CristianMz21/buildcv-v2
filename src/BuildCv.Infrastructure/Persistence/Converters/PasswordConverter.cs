using BuildCv.Domain.Identity;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// The hash is already a one-way, salted Argon2id digest, so it is stored as plaintext text; the
// column is not encrypted. Algorithm is derived from the hash string by the Domain factory and is
// never persisted separately.
internal sealed class PasswordConverter() : ValueConverter<Password, string>(
    password => password.Hash,
    hash => Password.Create(hash))
{
    public const int MaxLength = 256;
}
