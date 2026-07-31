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
            throw new EntryNotFoundException("Experience not found in resume.");
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
            ?? throw new EntryNotFoundException($"Skill '{skillName}' not found.");

        Skills = Skills.Where(s => s != skill).ToList();
        Touch();
    }

    public void AddEducation(Education education)
    {
        ArgumentNullException.ThrowIfNull(education);
        Educations = [.. Educations, education];
        Touch();
    }

    public void RemoveEducation(Education education)
    {
        ArgumentNullException.ThrowIfNull(education);
        var list = Educations.ToList();
        if (!list.Remove(education))
            throw new EntryNotFoundException("Education not found in resume.");
        Educations = list;
        Touch();
    }

    public void AddProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Projects = [.. Projects, project];
        Touch();
    }

    public void RemoveProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var list = Projects.ToList();
        if (!list.Remove(project))
            throw new EntryNotFoundException("Project not found in resume.");
        Projects = list;
        Touch();
    }

    public void AddCertificate(Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (Certificates.Any(c => c.Name.Equals(certificate.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DuplicateEntryException($"Certificate '{certificate.Name}' already exists.");

        Certificates = [.. Certificates, certificate];
        Touch();
    }

    public void RemoveCertificate(Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var list = Certificates.ToList();
        if (!list.Remove(certificate))
            throw new EntryNotFoundException("Certificate not found in resume.");
        Certificates = list;
        Touch();
    }

    public void AddLanguage(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (Languages.Any(l => l.Name.Equals(language.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DuplicateEntryException($"Language '{language.Name}' already exists.");

        Languages = [.. Languages, language];
        Touch();
    }

    public void RemoveLanguage(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var list = Languages.ToList();
        if (!list.Remove(language))
            throw new EntryNotFoundException("Language not found in resume.");
        Languages = list;
        Touch();
    }

    public void AddAward(Award award)
    {
        ArgumentNullException.ThrowIfNull(award);
        Awards = [.. Awards, award];
        Touch();
    }

    public void RemoveAward(Award award)
    {
        ArgumentNullException.ThrowIfNull(award);
        var list = Awards.ToList();
        if (!list.Remove(award))
            throw new EntryNotFoundException("Award not found in resume.");
        Awards = list;
        Touch();
    }

    public void AddPublication(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        Publications = [.. Publications, publication];
        Touch();
    }

    public void RemovePublication(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var list = Publications.ToList();
        if (!list.Remove(publication))
            throw new EntryNotFoundException("Publication not found in resume.");
        Publications = list;
        Touch();
    }

    public void AddInterest(Interest interest)
    {
        ArgumentNullException.ThrowIfNull(interest);
        if (Interests.Any(i => i.Name.Equals(interest.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DuplicateEntryException($"Interest '{interest.Name}' already exists.");

        Interests = [.. Interests, interest];
        Touch();
    }

    public void RemoveInterest(Interest interest)
    {
        ArgumentNullException.ThrowIfNull(interest);
        var list = Interests.ToList();
        if (!list.Remove(interest))
            throw new EntryNotFoundException("Interest not found in resume.");
        Interests = list;
        Touch();
    }

    public void AddReference(Reference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        References = [.. References, reference];
        Touch();
    }

    public void RemoveReference(Reference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var list = References.ToList();
        if (!list.Remove(reference))
            throw new EntryNotFoundException("Reference not found in resume.");
        References = list;
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
