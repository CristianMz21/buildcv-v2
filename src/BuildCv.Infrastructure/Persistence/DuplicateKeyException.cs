namespace BuildCv.Infrastructure.Persistence;

// Raised when a unique index rejects a write.
//
// This is the exception that makes the blind index enforceable. Registration checks for an existing
// account by EmailHash and then inserts; two concurrent registrations can both pass the check before
// either commits, and only the unique index on EmailHash stops the second one. Without a typed
// exception at this boundary, that race surfaces as a raw SqlException 2601/2627 and reads like an
// outage rather than "this address is taken".
public sealed class DuplicateKeyException : Exception
{
    public DuplicateKeyException(string message)
        : base(message)
    {
    }

    public DuplicateKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
