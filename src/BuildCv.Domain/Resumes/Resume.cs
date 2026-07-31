namespace BuildCv.Domain.Resumes;

public sealed class Resume
{
    public ContactInformation ContactInformation { get; private set; }
    public IReadOnlyList<Experience> Experiences { get; private set; }
    public IReadOnlyList<Education> Educations { get; private set; }
    public IReadOnlyList<Skill> Skills { get; private set; }
    public IReadOnlyList<Project> Projects { get; private set; }
    public IReadOnlyList<Certificate> Certificates { get; private set; }
    public IReadOnlyList<Language> Languages { get; private set; }
    public IReadOnlyList<Award> Awards { get; private set; }
    public IReadOnlyList<Publication> Publications { get; private set; }
    public IReadOnlyList<Interest> Interests { get; private set; }
    public IReadOnlyList<Reference> References { get; private set; }

    private Resume(ContactInformation contactInformation)
    {
        ContactInformation = contactInformation;
        Experiences = [];
        Educations = [];
        Skills = [];
        Projects = [];
        Certificates = [];
        Languages = [];
        Awards = [];
        Publications = [];
        Interests = [];
        References = [];
    }

    public static Resume Create(ContactInformation contactInformation) =>
        new(contactInformation);

    public void AddExperience(Experience experience) =>
        Experiences = [.. Experiences, experience];

    public void AddWorkExperience(Experience experience)
    {
        if (experience.Type != ExperienceType.Professional)
            throw new ArgumentException(
                "AddWorkExperience requires ExperienceType.Professional. Use AddExperience for other types.");

        AddExperience(experience);
    }

    public void RemoveWorkExperience(int index)
    {
        if (index < 0 || index >= Experiences.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var list = Experiences.ToList();
        list.RemoveAt(index);
        Experiences = list;
    }

    public void AddSkill(Skill skill)
    {
        if (Skills.Any(s => s.Name.Name.Equals(skill.Name.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Skill '{skill.Name}' already exists.");

        Skills = [.. Skills, skill];
    }

    public void RemoveSkill(string skillName)
    {
        var skill = Skills.FirstOrDefault(s => s.Name.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Skill '{skillName}' not found.");

        Skills = Skills.Where(s => s != skill).ToList();
    }

    public void UpdateContactInformation(ContactInformation contactInformation) =>
        ContactInformation = contactInformation;
}
