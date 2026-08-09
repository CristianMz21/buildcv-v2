using BuildCv.Infrastructure.Security;
using BuildCv.Infrastructure.Security.Encryption;
using BuildCv.Infrastructure.Tests.Security.Encryption;

namespace BuildCv.Infrastructure.Tests.Security;

// The REAL protector over a test key ring, not a fake. Everything in this assembly that needs one is
// exercising persistence or the protector itself, and both want the production construction: a fake here
// would leave the only thing worth checking — that a token this build mints is one this build accepts —
// unexercised in the assembly that owns it.
internal static class TestImportEvidence
{
    internal static ImportEvidenceProtector Protector(TimeProvider? timeProvider = null) =>
        Protector(EncryptionTestKeys.SingleBlindIndexRing(), timeProvider);

    internal static ImportEvidenceProtector Protector(
        BlindIndexKeyRing keyRing, TimeProvider? timeProvider = null) =>
        new(new HmacBlindIndex(keyRing), timeProvider ?? TimeProvider.System);
}
