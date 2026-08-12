namespace BuildCv.Application.Common.Repositories;

using BuildCv.Domain.Candidates;

/// <summary>
/// One candidate profile together with the identity of each of its collection entries.
/// </summary>
/// <remarks>
/// The mirror of <see cref="ResumeWithItemIds"/>, and the two travel together for the same reason: the
/// positional alignment <see cref="ResumeItemIds"/> promises is only true of the materialization it was
/// built from, so returning them separately would let a caller pair ids from one load with an aggregate
/// from another, and every position in that pairing would be a guess.
/// </remarks>
public sealed record CandidateProfileWithItemIds(CandidateProfile Profile, ResumeItemIds ItemIds);
