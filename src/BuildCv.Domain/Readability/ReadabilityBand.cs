namespace BuildCv.Domain.Readability;

// How readable a resume is, as one word.
//
// Its own enum rather than Scoring.ScoreBand, whose four members are spelled the same way. The members
// are not the point -- the SUBJECT is. A ScoreBand grades a match against one posting and moves when the
// posting changes; a ReadabilityBand grades a CV on its own and can only move when the candidate edits
// it. Sharing the type would put one name on two facts and invite exactly the blended figure this
// milestone refuses to compute: a client holding a `ScoreBand` from each engine has nothing telling it
// the two are not the same measurement.
public enum ReadabilityBand
{
    Low,
    Medium,
    Good,
    Strong
}
