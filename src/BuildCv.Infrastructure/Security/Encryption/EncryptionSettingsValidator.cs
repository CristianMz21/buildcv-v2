using Microsoft.Extensions.Options;

namespace BuildCv.Infrastructure.Security.Encryption;

// Paired with ValidateOnStart so a missing or malformed Encryption section takes the host down at
// boot instead of on the first request that touches an encrypted column. Delegates to the key ring
// so the startup check and the runtime construction can never disagree; a plain
// OptionsBuilder.Validate predicate was not used because it can only report one fixed message and
// operators need to know which key id is wrong.
internal sealed class EncryptionSettingsValidator : IValidateOptions<EncryptionSettings>
{
    public ValidateOptionsResult Validate(string? name, EncryptionSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Both rings, because a host that can encrypt but cannot compute a lookup digest is just as
        // unusable as one that cannot start at all — and far harder to diagnose in production.
        var error = EncryptionKeyRing.Validate(options) ?? BlindIndexKeyRing.Validate(options.BlindIndex);
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}
