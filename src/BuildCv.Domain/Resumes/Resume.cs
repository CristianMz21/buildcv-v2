using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

namespace BuildCv.Domain.Resumes;

public sealed class Resume
{
    public ResumeId Id { get; }
    public AccountId OwnerId { get; }
    public ContactInformation ContactInformation { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
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

    private Resume(ResumeId id, AccountId ownerId, ContactInformation contactInformation)
    {
        Id = id;
        OwnerId = ownerId;
        ContactInformation = contactInformation;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
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

    public static Resume Create(AccountId ownerId, ContactInformation contactInformation)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(contactInformation);
        return new Resume(ResumeId.New(), ownerId, contactInformation);
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    public void AddExperience(Experience experience)
    {
        ArgumentNullException.ThrowIfNull(experience);
        Experiences = [.. Experiences, experience];
        Touch();
    }

    public void AddWorkExperience(Experience experience)
    {
        ArgumentNullException.ThrowIfNull(experience);
        if (experience.Type != ExperienceType.Professional)
            throw new ArgumentException(
                "AddWorkExperience requires ExperienceType.Professional. Use AddExperience for other types.");
        AddExperience(experience);
    }

    public void RemoveExperience(Experience experience)
    {
        ArgumentNullException.ThrowIfNull(experience);
        var list = Experiences.ToList();
        if (!list.Remove(experience))
            throw new InvalidOperationException("Experience not found in resume.");
        Experiences = list;
        Touch();
    }

    public void RemoveWorkExperience(int index)
    {
        if (index < 0 || index >= Experiences.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var list = Experiences.ToList();
        list.RemoveAt(index);
        Experiences = list;
        Touch();
    }

    public void AddSkill(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (Skills.Any(s => s.Name.Name.Equals(skill.Name.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DuplicateSkillException($"Skill '{skill.Name}' already exists.");

        Skills = [.. Skills, skill];
        Touch();
    }

    public void RemoveSkill(string skillName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        var skill = Skills.FirstOrDefault(s => s.Name.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Skill '{skillName}' not found.");

        Skills = Skills.Where(s => s != skill).ToList();
        Touch();
    }

    public void UpdateContactInformation(ContactInformation contactInformation)
    {
        ArgumentNullException.ThrowIfNull(contactInformation);
        ContactInformation = contactInformation;
        Touch();
    }

    public override bool Equals(object? obj) => obj is Resume other && Id.Equals(other.Id);
    public override int GetHashCode() => Id.GetHashCode();
}
