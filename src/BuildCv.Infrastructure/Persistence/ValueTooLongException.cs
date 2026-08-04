namespace BuildCv.Infrastructure.Persistence;

// Raised when SQL Server refuses a write because a value does not fit its column (error 2628).
//
// DEFENCE IN DEPTH, not a live bug. Every bounded plaintext column on the model currently has a Domain
// rule that refuses an over-long value before a write is attempted — Language.Name against nvarchar(100)
// is the one that had to be added, after a 101-character name reached the database and came back as an
// untranslated 2628. This exists so the NEXT such column, added without its rule, is a 400 the caller
// can act on instead of a 500.
//
// IT CARRIES NO INNER EXCEPTION, and that is the whole point of translating it here. SQL Server's own
// 2628 message embeds the offending data — "String or binary data would be truncated in table 'X',
// column 'Y'. Truncated value: '<the candidate's text>'" — and PersistenceExceptionHandler logs the
// exception chain. Attaching the SqlException would put a candidate's own resume content into the
// application log, which is the exact harm this translation exists to stop. The cost is that the log
// does not name the column; the request path does narrow it, and a value long enough to trip this is
// reproducible.
public sealed class ValueTooLongException : Exception
{
    public ValueTooLongException(string message)
        : base(message)
    {
    }
}
