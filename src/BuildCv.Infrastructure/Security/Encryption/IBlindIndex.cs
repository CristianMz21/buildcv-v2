namespace BuildCv.Infrastructure.Security.Encryption;

// Deterministic 32-byte lookup token for values that are stored encrypted but still need equality
// search (Account.Email, RefreshToken.Token). The unique index goes on the hash column; never on
// the encrypted column, whose bytes differ on every write.
public interface IBlindIndex
{
    byte[] Compute(string value, string context);
}
