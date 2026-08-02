namespace BuildCv.Domain.Scoring;

// How badly a recommendation wants to be acted on. Persisted as a tinyint, so the numbers are
// explicit for the same reason SectionType's are.
public enum RecommendationPriority
{
    Critical = 0,
    Important = 1,
    NiceToHave = 2
}
