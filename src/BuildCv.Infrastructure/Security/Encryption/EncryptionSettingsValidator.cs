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

        var error = EncryptionKeyRing.Validate(options);
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}
