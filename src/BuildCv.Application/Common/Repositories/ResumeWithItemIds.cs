namespace BuildCv.Application.Common.Repositories;

using BuildCv.Domain.Resumes;

/// <summary>
/// One resume together with the identity of each of its collection entries.
/// </summary>
/// <remarks>
/// The two travel together because <see cref="ResumeItemIds"/>' positional alignment is only true of
/// the materialization it was built from. Returning them separately would let a caller pair ids from
/// one load with an aggregate from another, and every position in that pairing would be a guess.
/// </remarks>
public sealed record ResumeWithItemIds(Resume Resume, ResumeItemIds ItemIds);
