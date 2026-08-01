namespace BuildCv.Domain.Jobs;

// Persisted as tinyint (JobPostingConfiguration), so the numbers are stated rather than left to
// declaration order: inserting a member in the middle would renumber every member after it and
// every row already on disk would start reading back as something else.
public enum RequirementPriority
{
    MustHave = 0,
    NiceToHave = 1
}
