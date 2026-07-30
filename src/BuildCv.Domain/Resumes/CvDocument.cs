namespace BuildCv.Domain.Resumes;

public class CvDocument
{
    public required string FullName { get; init; }
    public required string Email { get; init; }
    public string? Phone { get; init; }
    public string? Summary { get; init; }
    public List<WorkExperience> Experience { get; init; } = [];
    public List<EducationEntry> Education { get; init; } = [];
    public List<string> Skills { get; init; } = [];
}

public class WorkExperience
{
    public required string Company { get; init; }
    public required string Position { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? Description { get; init; }
}

public class EducationEntry
{
    public required string Institution { get; init; }
    public required string Degree { get; init; }
    public string? Field { get; init; }
    public DateOnly? GraduationDate { get; init; }
}
