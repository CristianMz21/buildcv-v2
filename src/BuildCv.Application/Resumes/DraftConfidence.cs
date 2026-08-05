namespace BuildCv.Application.Resumes;

// Confidence is a PARALLEL structure, returned BESIDE the draft, never a field ON it. That separation
// is load-bearing, not stylistic:
//
//   - ResumeDraft is also the SUBMIT type — a client posts it back to POST /resumes/import. A confidence
//     field on the draft would mean the candidate posts confidence scores back to the server, which is
//     meaningless, and it would let a hostile client claim "High" confidence in nonsense to dress up a
//     bad submission.
//   - So the proposal carries the draft and this structure side by side, and only the draft crosses back.
//     Confidence is advice to the REVIEW SCREEN and one-directional by construction.
//
// The whole PR rests on one asymmetry: a field the parser is unsure about must arrive EMPTY and FLAGGED,
// never guessed and silent. FieldConfidence.NotExtracted is that flag — "the parser looked here and did
// not find it", paired with a null value on the draft. An empty field costs the candidate ten seconds of
// typing; a wrongly-populated field they do not notice corrupts their score invisibly. These are not
// symmetric, and NotExtracted is how the review screen tells them apart.

// How much of a field's value the parser is willing to stand behind. Ordered weakest to strongest so a
// review screen can compare (`>= Medium`) rather than switch.
public enum FieldConfidence
{
    // The parser looked for this field and did not find it, so the draft value is null. This is the
    // honest empty, and the review screen should show it as "please fill in" — NOT as a failure.
    NotExtracted = 0,

    // A value was extracted but it is a positional or structural GUESS the candidate must verify —
    // which text was the name, which half of a line was the employer. Wrong more often than right on the
    // hard 35% (two-column CVs, unusual layouts); flag it loudly.
    Low = 1,

    // Extracted by a rule that is usually right but has known failure modes — a phone-shaped run of
    // digits, the body of a recognised section. Verify, but expect it to be correct.
    Medium = 2,

    // Extracted by a rule that is almost always right on this kind of value — a well-formed email
    // address, a language proficiency word that maps exactly to the enum. Skim it.
    High = 3
}

// A hint for how far the review screen should trust the pre-filled draft AS A WHOLE. It is never a gate:
// the candidate reviews every field regardless, and a Low overall does not stop them submitting. It only
// changes the framing — Low means "treat this as a rough first pass, and consider pasting the text
// yourself instead", High means "mostly right, skim it".
public enum OverallConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>
/// Where one draft field came from and how much to trust it. <paramref name="Path"/> is the JSON field
/// path the review screen keys on — the same shape <c>POST /resumes/import</c> reports errors at
/// (<c>contact.email</c>, <c>experiences[0].start</c>) so one highlight map serves both screens.
/// </summary>
/// <param name="SourceText">
/// The raw snippet the parser reasoned about, when there is one worth showing — e.g. a start date left
/// blank because the source said only "2019" and no full date could be produced without inventing a
/// month and day. Lets the review screen show the candidate exactly what it saw. Null when there is
/// nothing to quote (a field that was simply not found, or one extracted verbatim).
/// </param>
public sealed record FieldProvenance(
    string Path,
    FieldConfidence Confidence,
    string? SourceText = null);

/// <summary>
/// The confidence and provenance that travel beside a proposed <see cref="ResumeDraft"/> — advice to the
/// review screen that is never posted back. See the file remarks for why this is separate from the draft.
/// </summary>
/// <param name="Fields">
/// One entry per field the parser reasoned about, INCLUDING fields it looked for and did not find
/// (<see cref="FieldConfidence.NotExtracted"/>). "Absent and flagged" is two assertions the review screen
/// can make together: the draft value is null AND a provenance entry says the parser looked.
/// </param>
/// <param name="Warnings">
/// Things to show the candidate prominently: a two-column layout that may have been read out of order, a
/// PDF with no text layer, a section heading the parser did not recognise. Empty when there is nothing
/// to say.
/// </param>
public sealed record DraftConfidence(
    OverallConfidence Overall,
    IReadOnlyList<FieldProvenance> Fields,
    IReadOnlyList<string> Warnings);

/// <summary>
/// A best-effort resume draft and the confidence beside it, as the extract step hands them to the review
/// screen. The submit path (<c>POST /resumes/import</c>) takes only <see cref="Draft"/>.
/// </summary>
public sealed record ResumeDraftProposal(
    ResumeDraft Draft,
    DraftConfidence Confidence);
