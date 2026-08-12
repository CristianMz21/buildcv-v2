namespace BuildCv.Domain.Candidates;

using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

/// <summary>
/// Everything a candidate has ever done, owned by the account rather than by any one CV.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the ten collections used to live inside <see cref="Resume"/>, and that made
/// the product's central promise unbuildable.</b> Three CVs meant the same job stored three times with
/// nothing linking them, information the candidate typed that was not in their PDF had nowhere to go
/// except inside one particular CV, and "generate the best CV for a backend role" had no source to
/// select from — it could only ask "from which of your CVs?", which is the question this product exists
/// not to ask.
/// </para>
/// <para>
/// <b>A CV is a COPY of this, never a live view of it.</b> That was decided by two facts in the code
/// rather than by preference. <c>ScoreResume.ScoredUnderTheSameConditions</c> invalidates a stored
/// analysis on <c>Resume.UpdatedAt</c>, so a live view would discard every score in the history the
/// first time somebody fixed a typo here — and that history is what answers "did acting on the advice
/// move the number", which is the product's whole argument. And the items have no identity:
/// <see cref="Experience"/> is a record, the ids the API exposes are synthesised at read time, and
/// removal addresses by POSITION precisely because six of these collections accept duplicates.
/// </para>
/// <para>
/// The product argument agrees and is the one to say out loud: <b>a CV you sent to a company is a
/// historical artifact.</b> If it changed retroactively you could not answer "what did they actually
/// receive?", which is the only question that matters when somebody calls you to interview.
/// </para>
/// <para>
/// <b>It is a superset, not a CV, and is deliberately not scored.</b> Readability and match scoring
/// judge a document against a page limit and a posting; this has neither and should grow without
/// penalty. Nothing here renormalises, caps or ranks — selecting is the generator's job.
/// </para>
/// <para>
/// <b>The item types are shared with <see cref="Resume"/> rather than copied.</b> One definition of
/// <see cref="Experience"/>, one of <see cref="Skill"/>: two parallel sets would drift on the first
/// change to either, and copying between profile and CV would then need a translation layer that can
/// silently lose a field.
/// </para>
/// </remarks>
public sealed class CandidateProfile
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

    public CandidateProfileId Id { get; }

    /// <summary>The account this profile belongs to. One profile per account, enforced by a unique index.</summary>
    public AccountId OwnerId { get; }

    public ContactInformation ContactInformation { get; private set; }

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

    private CandidateProfile(
        CandidateProfileId id, AccountId ownerId, ContactInformation contactInformation)
    {
        var now = DateTimeOffset.UtcNow;
        Id = id;
        OwnerId = ownerId;
        ContactInformation = contactInformation;
        CreatedAt = now;
        UpdatedAt = now;
    }

#pragma warning disable CS8618 // EF Core assigns every mapped member immediately after construction.
    private CandidateProfile() { }
#pragma warning restore CS8618

    public static CandidateProfile Create(AccountId ownerId, ContactInformation contactInformation)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(contactInformation);
        return new CandidateProfile(CandidateProfileId.New(), ownerId, contactInformation);
    }

    public void UpdateContactInformation(ContactInformation contactInformation)
    {
        ArgumentNullException.ThrowIfNull(contactInformation);
        if (ContactInformation == contactInformation)
            return;
        ContactInformation = contactInformation;
        Touch();
    }

    /// <summary>
    /// Adds an experience, ignoring one this profile already holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every Add here is idempotent, which is the difference from <see cref="Resume"/>'s.</b> A CV is
    /// authored once and its duplicate rules exist to catch a candidate typing the same skill twice; a
    /// profile is written to repeatedly and from several directions — a second CV imported, a field
    /// filled in by hand, the same PDF uploaded again after a correction. Refusing a duplicate there
    /// would make importing a second CV fail on everything the two share, which is most of it.
    /// </para>
    /// <para>
    /// So a repeat is a no-op rather than an error, and the caller does not have to know what is already
    /// here. Equality is the record's own: these are value objects, so "the same job" means every field
    /// matches, and a role whose dates or bullets differ is a different entry the candidate can merge or
    /// delete. That is deliberately conservative — a false duplicate silently loses information the
    /// person gave us, which is the one thing this aggregate exists to prevent.
    /// </para>
    /// </remarks>
    public void AddExperience(Experience experience) => AddDistinct(_experiences, experience);

    public void AddEducation(Education education) => AddDistinct(_educations, education);

    public void AddSkill(Skill skill) => AddDistinct(_skills, skill);

    public void AddProject(Project project) => AddDistinct(_projects, project);

    public void AddCertificate(Certificate certificate) => AddDistinct(_certificates, certificate);

    public void AddLanguage(Language language) => AddDistinct(_languages, language);

    public void AddAward(Award award) => AddDistinct(_awards, award);

    public void AddPublication(Publication publication) => AddDistinct(_publications, publication);

    public void AddInterest(Interest interest) => AddDistinct(_interests, interest);

    public void AddReference(Reference reference) => AddDistinct(_references, reference);

    /// <summary>
    /// Removes the entry at <paramref name="index"/> of the named collection.
    /// </summary>
    /// <remarks>
    /// BY POSITION, exactly as <c>ResumeItems.RemoveAt</c> does, and for the same reason: these are value
    /// objects with no identity, so removing "by value" would delete an entry the caller never named
    /// whenever two of them happen to be equal.
    /// </remarks>
    public bool RemoveExperienceAt(int index) => RemoveAt(_experiences, index);

    public bool RemoveEducationAt(int index) => RemoveAt(_educations, index);

    public bool RemoveSkillAt(int index) => RemoveAt(_skills, index);

    public bool RemoveProjectAt(int index) => RemoveAt(_projects, index);

    public bool RemoveCertificateAt(int index) => RemoveAt(_certificates, index);

    public bool RemoveLanguageAt(int index) => RemoveAt(_languages, index);

    public bool RemoveAwardAt(int index) => RemoveAt(_awards, index);

    public bool RemovePublicationAt(int index) => RemoveAt(_publications, index);

    public bool RemoveInterestAt(int index) => RemoveAt(_interests, index);

    public bool RemoveReferenceAt(int index) => RemoveAt(_references, index);

    private void AddDistinct<T>(List<T> items, T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (items.Contains(item))
            return;

        items.Add(item);
        Touch();
    }

    private bool RemoveAt<T>(List<T> items, int index)
    {
        if (index < 0 || index >= items.Count)
            return false;

        items.RemoveAt(index);
        Touch();
        return true;
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    public override bool Equals(object? obj) => obj is CandidateProfile other && Id.Equals(other.Id);

    public override int GetHashCode() => Id.GetHashCode();
}
