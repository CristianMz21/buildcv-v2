namespace BuildCv.Application.Scoring;

using BuildCv.Domain.Jobs;

/// <summary>
/// Which of the candidate's entries answered a posting's requirement, and with what text.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a client CANNOT derive it. <see cref="ScoringRules.IsSatisfiedBy"/> canonicalizes
/// through <c>SkillLexicon.txt</c>, an embedded resource served by no endpoint, so "React.js" satisfying
/// "React" is knowledge that lives only inside this process. A client comparing strings would contradict
/// the score printed beside it — not occasionally, but exactly whenever the lexicon did its job. Publishing
/// the attribution is what keeps the matching rule stated once, on the server.
/// </para>
/// <para>
/// IT IS NEVER PERSISTED, and that is the design rather than a shortcut. An <c>Analysis</c> is a historical
/// fact that outlives the resume it scored — <c>IsStale</c> exists to say so. Attribution recomputed when a
/// stored analysis is read would describe the resume as it is NOW while sitting beside a score computed
/// from what it WAS, and a client could not tell. So it is returned only by the call that computed it,
/// against the snapshot it computed from, and <c>GET /v1/scoring/{analysisId}</c> and the history carry it
/// nowhere. That separation is enforced by the type system rather than by convention: this travels on
/// <see cref="ScoredAnalysisView"/>, which only <c>ScoreResumeHandler</c> returns, and never on
/// <see cref="AnalysisView"/>, which all three endpoints share.
/// </para>
/// <para>
/// De-duplication does not weaken any of that, and the reason is worth stating because the opposite is the
/// intuitive guess. <c>ScoreResume</c> reuses a stored analysis only when
/// <c>existing.ResumeUpdatedAt == resume.UpdatedAt</c>, so a reuse is PROOF the resume has not moved —
/// attribution computed now is attribution as it was then. A resume that changed does not de-duplicate; it
/// is re-scored. Both branches therefore answer with attribution, and neither can disagree with its score.
/// </para>
/// </remarks>
public sealed record RequirementAttribution(
    string Skill,
    RequirementPriority Priority,
    double Weight,
    bool Satisfied,
    IReadOnlyList<RequirementEvidence> MatchedBy);

/// <summary>
/// One place in the resume that answered a requirement, and the text that did it.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="MatchedText"/> is the candidate's own wording, verbatim — it is what makes the lexicon's
/// work VISIBLE instead of magical. A candidate told their "React.js" satisfied a "React" requirement can
/// see why; a candidate told only that it matched has to trust it.
/// </para>
/// <para>
/// THERE IS NO ENTRY ID, deliberately. Entry ids are not carried by the domain at all: they come from
/// <c>ResumeItemIds</c>, whose positional alignment holds only within a single materialization, and reading
/// them needs the tracked-entity path that the scoring load has no other reason to take. It costs the
/// client nothing here — <c>Resume.AddSkill</c> refuses a duplicate name, so skill names are unique within
/// one CV and a <see cref="RequirementMatchSource.SkillName"/> match joins back by exact string.
/// </para>
/// </remarks>
public sealed record RequirementEvidence(RequirementMatchSource Source, string MatchedText);

/// <summary>
/// The three places a requirement is compared against a resume — the whole list, not a sample.
/// </summary>
/// <remarks>
/// EXPERIENCES ARE NOT HERE AND CANNOT BE. <see cref="ScoringRules.IsSatisfiedBy"/> reads skill names,
/// the keywords beside them, and the technologies on a project; it never reads an experience. So a client
/// cannot rank work history by requirements answered, from this or from anything else the engine knows —
/// the engine does not look there. Adding experiences to this enum would mean teaching the matcher to read
/// them, which changes every score in the system and is a scoring decision, not a serialization one.
/// </remarks>
public enum RequirementMatchSource
{
    SkillName = 0,
    SkillKeyword = 1,
    ProjectTechnology = 2
}
