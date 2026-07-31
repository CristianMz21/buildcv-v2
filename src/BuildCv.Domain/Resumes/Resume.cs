namespace BuildCv.Domain.Resumes;

public sealed record Resume(
    Basics Basics,
    IReadOnlyList<WorkExperience> WorkExperiences,
    IReadOnlyList<Education> Educations,
    IReadOnlyList<Skill> Skills,
    IReadOnlyList<Project> Projects,
    IReadOnlyList<Certificate> Certificates,
    IReadOnlyList<Language> Languages,
    IReadOnlyList<Award> Awards,
    IReadOnlyList<Publication> Publications,
    IReadOnlyList<VolunteerExperience> VolunteerExperiences,
    IReadOnlyList<Interest> Interests,
    IReadOnlyList<Reference> References);
