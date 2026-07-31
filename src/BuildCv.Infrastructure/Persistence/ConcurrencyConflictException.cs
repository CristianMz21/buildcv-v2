namespace BuildCv.Infrastructure.Persistence;

// Raised when a rowversion check finds the row moved under a writer.
//
// Infrastructure-level on purpose: the Domain has no concept of optimistic concurrency, and giving
// it one would leak the storage strategy inward. Task 4's repositories translate
// DbUpdateConcurrencyException into this so callers do not have to reference EF Core to catch it.
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
