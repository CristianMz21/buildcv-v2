namespace BuildCv.Application.Common.Services;

using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

/// <summary>
/// Turns <see cref="ImportSignals"/> into an opaque token a client can hold and hand back, and turns one
/// of those tokens back into signals — but only for the account it was issued to, and only while it is
/// still fresh.
/// </summary>
/// <remarks>
/// <para>
/// WHY A TOKEN AND NOT A ROW. The extraction endpoints write NOTHING, by construction, and that is the
/// guarantee this feature had to be built around: a draft row written at extract time would have no
/// garbage collector, because this system deliberately has no background jobs, so every abandoned upload
/// would be a permanent row about a CV nobody imported. Handing the evidence to the client and taking it
/// back signed keeps the write count at zero and needs no reaper.
/// </para>
/// <para>
/// WHY SIGNED AND NOT TRUSTED. <c>DraftConfidence</c> already states the rule this follows: anything a
/// client posts back is client-asserted and forgeable. Import signals feed a SCORE, so a candidate who
/// could hand-write their own would be able to claim a perfectly parseable document for a scanned
/// two-column PDF. That is self-harm on an advisory number, and it would also make the section
/// meaningless for everyone, because a score anybody can set is not a measurement.
/// </para>
/// <para>
/// WHAT IT DOES NOT PREVENT, stated because the alternative is implying otherwise. A candidate can take a
/// token minted for one of their own uploads and import a DIFFERENT resume with it — the binding is to
/// the account, not to a resume that does not exist yet at extract time. The blast radius is one
/// candidate's own advisory number, and closing it would mean persisting something at extract time, which
/// is the write this design exists to avoid.
/// </para>
/// </remarks>
public interface IImportEvidenceProtector
{
    /// <summary>
    /// How long a token stays acceptable. Long enough to review and correct forty-odd extracted fields
    /// without racing a clock; short enough that a token captured from a log or a browser history is not
    /// a durable capability. It is not a security boundary on its own — the token is account-bound and
    /// grants nothing but a claim about one's own document — so it is sized for the review screen.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    /// <summary>
    /// The message a candidate is shown when the token is past <see cref="Lifetime"/>. Separated from
    /// <see cref="InvalidTokenError"/> because it names a different fix and leaks nothing: a caller
    /// holding an expired token of their own already knows it was valid.
    /// </summary>
    public const string ExpiredTokenError =
        "The upload evidence has expired. Upload the document again to refresh it, or submit without it.";

    /// <summary>
    /// The message for every other rejection — a bad signature, a truncated or re-encoded token, or one
    /// issued to another account. ONE message for all of them on purpose: splitting it would tell a
    /// caller probing tokens which part of the check they got past.
    /// </summary>
    public const string InvalidTokenError =
        "The upload evidence is not valid. Upload the document again, or submit without it.";

    /// <summary>Signs one set of signals for one account, as of now.</summary>
    string Protect(ImportSignals signals, AccountId accountId);

    /// <summary>
    /// Verifies a token and returns the signals it carries, or the reason it was refused. The failure is
    /// always one of the two constants above, so the wording lives in one place.
    /// </summary>
    Result<ImportSignals> Unprotect(string token, AccountId accountId);
}
