namespace BuildCv.Domain.Identity;

using BuildCv.Domain.Exceptions;

/// <summary>
/// What a candidate is allowed to choose as a password.
/// </summary>
/// <remarks>
/// <para>
/// A STATIC CHECK RATHER THAN A VALUE OBJECT, which is the opposite of what the rest of this
/// Domain does, on purpose. <see cref="Password"/> wraps a HASH — by the time a plaintext
/// password reaches it, hashing has already happened and the policy has nothing left to inspect.
/// The obvious fix is a second value object wrapping the plaintext, and that is the wrong shape:
/// it gives a raw credential a type that can be held, passed, logged and serialized. This value
/// is validated and hashed within two statements and is never stored, so nothing needs to carry
/// it. <see cref="Password.ToString"/> already redacts for exactly this reason; the cheaper
/// answer is to not create the second thing that needs redacting.
/// </para>
/// <para>
/// NO COMPOSITION RULES, following NIST SP 800-63B. Requiring an uppercase letter and a symbol
/// produces "Password1!" — it costs the user real friction and buys less entropy than the same
/// characters spent on length. Length is the whole policy.
/// </para>
/// <para>
/// THE UPPER BOUND IS A COST CONTROL, not security theatre. Argon2id hashes the entire input
/// with the configured memory and iteration parameters, so an unbounded password is a request
/// whose server-side cost the caller picks. <see cref="MaxLength"/> is far above any real
/// passphrase and far below anything that would matter.
/// </para>
/// <para>
/// KNOWN-COMPROMISED PASSWORD CHECKING IS THE GAP, and it is named rather than quietly absent.
/// NIST rates a breach-corpus check above any length rule, and this policy accepts
/// "123456789012". Closing it needs a real corpus (HIBP's range API, or a shipped list) and is
/// its own change with its own network and privacy questions.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>
    /// Twelve, for a product with no second factor and no email verification. NIST's floor is 8
    /// and OWASP suggests 15 where a password is the only factor; this sits between them because
    /// the account being protected holds a CV rather than money, and a floor high enough to be
    /// refused is a floor that sends people to a password they reused somewhere else.
    /// </summary>
    public const int MinLength = 12;

    /// <summary>See the remarks on <see cref="PasswordPolicy"/>: this bounds hashing cost.</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Throws <see cref="WeakPasswordException"/> if <paramref name="password"/> may not be used.
    /// </summary>
    /// <remarks>
    /// Whitespace is NOT trimmed and is counted like any other character — a passphrase with
    /// spaces in it is a good password, and silently trimming would mean a password that
    /// verifies here and fails at the next login against a client that trims differently. Only
    /// an entirely blank value is refused, and it is refused as too short rather than as a
    /// separate case, so the caller is told the one thing that helps.
    /// </remarks>
    public static void Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinLength)
            throw new WeakPasswordException($"Password must be at least {MinLength} characters.");

        if (password.Length > MaxLength)
            throw new WeakPasswordException($"Password must be at most {MaxLength} characters.");
    }
}
