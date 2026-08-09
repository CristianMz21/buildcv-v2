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

    /// <summary>
    /// The maximum length of <see cref="Name"/>.
    /// </summary>
    /// <remarks>
    /// PRODUCT POLICY, NOT A PERSISTENCE NECESSITY, and the difference is worth stating so nobody
    /// cites the wrong precedent. <c>Language.Name</c> is capped because it is the one PLAINTEXT
    /// BOUNDED column in the resume graph and a longer value reaches SQL Server as error 2628. This
    /// column is an encrypted <c>varbinary(max)</c>: it cannot overflow, and no rule here is holding
    /// back a truncation error. The cap exists because a label is a label — something that fits in a
    /// picker row — and because unbounded free text on a field nothing reads is storage a candidate
    /// can grow without limit.
    /// </remarks>
    public const int NameMaxLength = 120;

    public ResumeId Id { get; }
    public AccountId OwnerId { get; }

    /// <summary>
    /// What the candidate calls this CV — "Backend roles", "CV para Google" — or null when they have
    /// not named it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT EXISTS BECAUSE NOTHING ELSE DISTINGUISHES TWO CVs OF ONE PERSON. Every other candidate for a
    /// label is either identical across them (the contact name) or absent from the list projection
    /// (the most recent job title, which lives in a collection <c>GET /v1/resumes</c> does not carry).
    /// A picker showing the same words on every row is the failure this closes.
    /// </para>
    /// <para>
    /// NULLABLE, and a CV with no name is not second-class: every CV that exists today has none, and
    /// naming one is a convenience rather than a step. Callers fall back to the contact name.
    /// </para>
    /// <para>
    /// Encrypted at rest. It is free text a candidate writes about themselves and their search —
    /// "CV para la entrevista en Globant" names an employer they have told nobody — and it passes the
    /// test this repository actually applies to the question: nothing queries it. No engine reads it,
    /// no index needs it, and no analytics groups by it.
    /// </para>
    /// </remarks>
    public string? Name { get; private set; }

    public ContactInformation ContactInformation { get; private set; }

    /// <summary>
    /// What the document this resume was imported from looked like to a parser, or null when it was not
    /// imported from one — built by hand, or imported without evidence.
    /// </summary>
    /// <remarks>
    /// WRITE-ONCE, AND ONLY AT CREATION. There is no mutator and no setter outside the factory, which is
    /// what makes the signals evidence about THIS resume's own source document rather than a value that
    /// could later be pointed at any resume. Null is the ordinary case and the readability engine
    /// renormalizes the ATS-parseability section out for it, so a hand-built CV is neither credited nor
    /// penalised for a document that does not exist.
    /// </remarks>
    public ImportSignals? ImportSignals { get; private set; }

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<Experience> Experiences => _experiences.AsReadOnly();
    public IReadOnlyList<Education> Educations => _educations.AsReadOnly();
    public IReadOnlyList<Skill> Skills => _skills.AsReadOnly();
    public IReadOnlyList<Project> Projects => _projects.AsReadOnly();
    public IReadOnlyList<Certificate> Certificates => _certificates.AsReadOnly();
    public IReadOnlyList<Language> Languages => _languages.AsReadOnly();
    public IReadOnlyList<Award> Awards => _awards.AsReadOnly();
    public IReadOnlyList<Publication> Publications => _publications.AsReadOnly();
    public IReadOnlyList<Interest> Interests => _interests.AsReadOnly();
    public IReadOnlyList<Reference> References => _references.AsReadOnly();

    private Resume(
        ResumeId id, AccountId ownerId, ContactInformation contactInformation, ImportSignals? importSignals)
    {
        Id = id;
        OwnerId = ownerId;
        ContactInformation = contactInformation;
        ImportSignals = importSignals;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

#pragma warning disable CS8618 // EF Core assigns every mapped member immediately after construction.
    private Resume() { }
#pragma warning restore CS8618

    // importSignals is optional and defaults to null, so every existing caller keeps meaning what it
    // meant: a resume created by any route other than a document import has no document to describe.
    public static Resume Create(
        AccountId ownerId, ContactInformation contactInformation, ImportSignals? importSignals = null)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(contactInformation);
        return new Resume(ResumeId.New(), ownerId, contactInformation, importSignals);
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
        // NAMES THE POSITION, NOT THE VALUE. This message is surfaced verbatim in the 400 body of
        // POST /resumes/import, keyed by the field path of the LATER occurrence, so the value in it was
        // pure repetition — and Certificate.Name and Interest.Name are classified CONFIDENTIAL and
        // encrypted at rest, which made echoing them back in an error string the wrong default. The
        // index is the earlier entry, so a review screen can highlight both rows.
        var duplicateSkill = _skills.FindIndex(
            s => s.Name.Name.Equals(skill.Name.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicateSkill >= 0)
            throw new DuplicateSkillException($"Duplicates the skill at index {duplicateSkill}.");

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
        var duplicateCertificate = _certificates.FindIndex(
            c => c.Name.Equals(certificate.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicateCertificate >= 0)
            throw new DuplicateEntryException($"Duplicates the certificate at index {duplicateCertificate}.");

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
        var duplicateLanguage = _languages.FindIndex(
            l => l.Name.Equals(language.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicateLanguage >= 0)
            throw new DuplicateEntryException($"Duplicates the language at index {duplicateLanguage}.");

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
        var duplicateInterest = _interests.FindIndex(
            i => i.Name.Equals(interest.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicateInterest >= 0)
            throw new DuplicateEntryException($"Duplicates the interest at index {duplicateInterest}.");

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

    // ── Removal by position ───────────────────────────────────────────────────────────────────────
    //
    // THE Remove*(T) OVERLOADS ABOVE CANNOT DELETE THE ENTRY YOU MEAN when two hold the same value.
    // They go through List.Remove, which removes the FIRST structurally-equal element, and six of these
    // ten collections accept duplicates — only skills, certificates, languages and interests are
    // name-unique. So asking to remove the second of two identical awards removes the first instead.
    //
    // The aggregate lands in the same state either way, which is why nothing here ever looked wrong.
    // What differs is WHICH ROW the store deletes: EF removes the row of the instance that left the
    // collection, so the surviving entry keeps the id the caller asked to delete. A client is then told
    // its delete succeeded while the id it named is still in the next response, and the entry that
    // vanished is one it never mentioned.
    //
    // Position is what the caller actually resolved — an id names an entry, and an entry sits at an
    // index — so these take it directly. RemoveWorkExperience(int) already established the shape; it is
    // left alone because its ArgumentOutOfRangeException is part of an existing contract, while an
    // out-of-range index HERE means "no such entry", which is a not-found.
    public void RemoveExperienceAt(int index) => RemoveAt(_experiences, index, "Experience");
    public void RemoveEducationAt(int index) => RemoveAt(_educations, index, "Education");
    public void RemoveSkillAt(int index) => RemoveAt(_skills, index, "Skill");
    public void RemoveProjectAt(int index) => RemoveAt(_projects, index, "Project");
    public void RemoveCertificateAt(int index) => RemoveAt(_certificates, index, "Certificate");
    public void RemoveLanguageAt(int index) => RemoveAt(_languages, index, "Language");
    public void RemoveAwardAt(int index) => RemoveAt(_awards, index, "Award");
    public void RemovePublicationAt(int index) => RemoveAt(_publications, index, "Publication");
    public void RemoveInterestAt(int index) => RemoveAt(_interests, index, "Interest");
    public void RemoveReferenceAt(int index) => RemoveAt(_references, index, "Reference");

    private void RemoveAt<T>(List<T> items, int index, string entryName)
    {
        if (index < 0 || index >= items.Count)
            throw new EntryNotFoundException($"{entryName} not found in resume.");

        items.RemoveAt(index);
        Touch();
    }

    /// <summary>
    /// Names this CV, or clears the name when given null or blank.
    /// </summary>
    /// <remarks>
    /// BLANK CLEARS RATHER THAN STORES. "Not named" and "named the empty string" would be two states
    /// a candidate cannot tell apart on screen and every caller would have to collapse anyway, so the
    /// aggregate collapses them once. Surrounding whitespace goes with it: a name is a label, and a
    /// trailing space is not part of one.
    /// </remarks>
    public void Rename(string? name)
    {
        var trimmed = name?.Trim();

        if (trimmed?.Length > NameMaxLength)
        {
            // NAMES THE LIMIT, NOT THE VALUE. This message reaches a 400 body and the application
            // log, and the value is a candidate's own words about their job search.
            throw new InvalidResumeNameException($"Name exceeds {NameMaxLength} characters.");
        }

        Name = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
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
