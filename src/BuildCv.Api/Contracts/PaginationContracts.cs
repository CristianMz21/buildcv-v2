namespace BuildCv.Api.Contracts;

// The wire shape of a paged list, kept separate from Application's Page<T> for the usual reason: the
// two are free to diverge, and a rename inside the Application layer must not silently become a
// breaking change for every client.
//
// NextCursor is null when this is the last page. It is the ONLY thing a client should use to ask for
// more — the value is opaque and its encoding is the server's to change.
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, string? NextCursor);
