using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

namespace BuildCv.Domain.Resumes;

public sealed class Resume
{
    private readonly List<Experience> _experiences = [];
    private readonly List<Education> _educations = [];
    private readonly List<Skill> _skills = [];
    private readonly List<Project> _projects = [];
    private readonly List<Certificate> _certificates = [];
    private readonly List<Language> _languages = [];
    private readonly List<Award> _awards = [];
    private readonly List<Publication> _publications = [];
    private readonly List<Interest> _interests = [];
    private readonly List<Reference> _references = [];

    public ResumeId Id { get; }
    public AccountId OwnerId { get; }
    public ContactInformation ContactInformation { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<Experience> Experiences => _experiences;
    public IReadOnlyList<Education> Educations => _educations;
    public IReadOnlyList<Skill> Skills => _skills;
    public IReadOnlyList<Project> Projects => _projects;
    public IReadOnlyList<Certificate> Certificates => _certificates;
    public IReadOnlyList<Language> Languages => _languages;
    public IReadOnlyList<Award> Awards => _awards;
    public IReadOnlyList<Publication> Publications => _publications;
    public IReadOnlyList<Interest> Interests => _interests;
    public IReadOnlyList<Reference> References => _references;

    private Resume(ResumeId id, AccountId ownerId, ContactInformation contactInformation)
    {
        Id = id;
        OwnerId = ownerId;
        ContactInformation = contactInformation;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

#pragma warning disable CS8618 // EF Core assigns every mapped member immediately after construction.
    private Resume() { }
#pragma warning restore CS8618

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
        _experiences.Add(experience);
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
        if (!_experiences.Remove(experience))
            throw new EntryNotFoundException("Experience not found in resume.");
        Touch();
    }

    public void RemoveWorkExperience(int index)
    {
        if (index < 0 || index >= _experiences.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        _experiences.RemoveAt(index);
        Touch();
    }

    public void AddSkill(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (_skills.Any(s => s.Name.Name.Equals(skill.Name.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DuplicateSkillException($"Skill '{skill.Name}' already exists.");

        _skills.Add(skill);
        Touch();
    }

    public void RemoveSkill(string skillName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        var skill = _skills.FirstOrDefault(s => s.Name.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase))
            ?? throw new EntryNotFoundException($"Skill '{skillName}' not found.");

        _skills.Remove(skill);
        Touch();
    }

    public void AddEducation(Education education)
    {
        ArgumentNullException.ThrowIfNull(education);
        _educations.Add(education);
        Touch();
    }

    public void RemoveEducation(Education education)
    {
        ArgumentNullException.ThrowIfNull(education);
        if (!_educations.Remove(education))
            throw new EntryNotFoundException("Education not found in resume.");
        Touch();
    }

    public void AddProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _projects.Add(project);
        Touch();
    }

    public void RemoveProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!_projects.Remove(project))
            throw new EntryNotFoundException("Project not found in resume.");
        Touch();
    }

    public void AddCertificate(Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (_certificates.Any(c => c.Name.Equals(certificate.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DuplicateEntryException($"Certificate '{certificate.Name}' already exists.");

        _certificates.Add(certificate);
        Touch();
    }

    public void RemoveCertificate(Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!_certificates.Remove(certificate))
            throw new EntryNotFoundException("Certificate not found in resume.");
        Touch();
    }

    public void AddLanguage(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (_languages.Any(l => l.Name.Equals(language.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DuplicateEntryException($"Language '{language.Name}' already exists.");

        _languages.Add(language);
        Touch();
    }

    public void RemoveLanguage(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (!_languages.Remove(language))
            throw new EntryNotFoundException("Language not found in resume.");
        Touch();
    }

    public void AddAward(Award award)
    {
        ArgumentNullException.ThrowIfNull(award);
        _awards.Add(award);
        Touch();
    }

    public void RemoveAward(Award award)
    {
        ArgumentNullException.ThrowIfNull(award);
        if (!_awards.Remove(award))
            throw new EntryNotFoundException("Award not found in resume.");
        Touch();
    }

    public void AddPublication(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        _publications.Add(publication);
        Touch();
    }

    public void RemovePublication(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!_publications.Remove(publication))
            throw new EntryNotFoundException("Publication not found in resume.");
        Touch();
    }

    public void AddInterest(Interest interest)
    {
        ArgumentNullException.ThrowIfNull(interest);
        if (_interests.Any(i => i.Name.Equals(interest.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DuplicateEntryException($"Interest '{interest.Name}' already exists.");

        _interests.Add(interest);
        Touch();
    }

    public void RemoveInterest(Interest interest)
    {
        ArgumentNullException.ThrowIfNull(interest);
        if (!_interests.Remove(interest))
            throw new EntryNotFoundException("Interest not found in resume.");
        Touch();
    }

    public void AddReference(Reference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _references.Add(reference);
        Touch();
    }

    public void RemoveReference(Reference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!_references.Remove(reference))
            throw new EntryNotFoundException("Reference not found in resume.");
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
