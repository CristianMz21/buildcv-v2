using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Infrastructure.Security.Encryption;

namespace BuildCv.Infrastructure.Persistence.BlindIndexes;

// The only supported way to compute the Accounts.EmailHash digest.
//
// IBlindIndex.Compute takes its value verbatim, by design: normalization belongs to the Domain, and
// re-normalizing inside the HMAC would let the two disagree about what equality means. That is
// coherent only if every caller hands it the normalized form. Email.Create lower-cases; a raw
// request string does not — so registering "USER@Example.com" and logging in with "user@example.com"
// would write one digest and look up another, the unique index would not fire, and the login would
// read as "no such account". Nothing about that failure looks like a bug in a log.
//
// So the parameter is Email, not string. A caller holding request.Email cannot reach this method
// without going through Email.Create first, and the compiler enforces it rather than a comment.
public sealed class AccountEmailIndex
{
    // Must match the AAD context on the encrypted Accounts.Email column. ModelConfigurationTests
    // asserts the pairing, so a rename here that misses the configuration fails the build's tests
    // rather than silently orphaning every stored digest.
    public const string Context = "Account.Email";

    private readonly IBlindIndex _blindIndex;

    public AccountEmailIndex(IBlindIndex blindIndex)
    {
        ArgumentNullException.ThrowIfNull(blindIndex);
        _blindIndex = blindIndex;
    }

    // The digest to WRITE, under the active blind-index key.
    public byte[] Compute(Email email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return _blindIndex.Compute(email.Value, Context);
    }

    // The digests to MATCH, one per configured key. A lookup must use this and not Compute: during a
    // key rotation, rows written under the retired key still have to be findable, and "not found" on
    // an existing account is exactly the failure that lets a duplicate registration through.
    public IReadOnlyList<byte[]> ComputeCandidates(Email email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return _blindIndex.ComputeCandidates(email.Value, Context);
    }
}
