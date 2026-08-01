using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

namespace BuildCv.Domain.Jobs;

public sealed class JobPosting
{
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 5000;

    private readonly List<JobRequirement> _requirements = [];
    private readonly List<Responsibility> _responsibilities = [];
    private readonly List<LanguageRequirement> _languageRequirements = [];

    public JobPostingId Id { get; }
    public AccountId OwnerId { get; }
    public string Title { get; private set; }
    public string? Description { get; private set; }
    public OrganizationId? CompanyId { get; }
    public OrganizationName? CompanyName { get; }
    public JobPostingStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public DateTimeOffset? ClosesAt { get; private set; }
    public IReadOnlyList<JobRequirement> Requirements => _requirements.AsReadOnly();
    public IReadOnlyList<Responsibility> Responsibilities => _responsibilities.AsReadOnly();
    public IReadOnlyList<LanguageRequirement> LanguageRequirements => _languageRequirements.AsReadOnly();

    // Nullable because most postings state no degree requirement at all, and "not stated" has to stay
    // distinguishable from "high school": PR 3 penalises a candidate for missing a stated requirement
    // and must not invent one. There is no Domain mutator yet on purpose -- job-side authoring is
    // recruiter-facing and this phase is candidate-first, so the column exists for the scorer to read
    // and the write path arrives with the "bring your own job offer" phase. EF materializes it.
    public EducationLevel? EducationLevel { get; private set; }

    private JobPosting(
        JobPostingId id,
        AccountId ownerId,
        string title,
        string? description,
        OrganizationId? companyId,
        OrganizationName? companyName)
    {
        Id = id;
        OwnerId = ownerId;
        Title = title;
        Description = description;
        CompanyId = companyId;
        CompanyName = companyName;
        Status = JobPostingStatus.Draft;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static JobPosting Create(AccountId ownerId, string title, OrganizationName companyName, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(companyName);
        var validatedTitle = ValidateTitle(title);
        var validatedDescription = ValidateDescription(description);
        return new JobPosting(JobPostingId.New(), ownerId, validatedTitle, validatedDescription, null, companyName);
    }

    public static JobPosting CreateForOrganization(AccountId ownerId, OrganizationId companyOrgId, string title, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(companyOrgId);
        var validatedTitle = ValidateTitle(title);
        var validatedDescription = ValidateDescription(description);
        return new JobPosting(JobPostingId.New(), ownerId, validatedTitle, validatedDescription, companyOrgId, null);
    }

    private static string ValidateTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var trimmed = title.Trim();
        if (trimmed.Length > MaxTitleLength)
            throw new InvalidJobPostingException($"Title exceeds {MaxTitleLength} characters.");
        return trimmed;
    }

    private static string? ValidateDescription(string? description)
    {
        if (description is null)
            return null;
        var trimmed = description.Trim();
        if (trimmed.Length > MaxDescriptionLength)
            throw new InvalidJobPostingException($"Description exceeds {MaxDescriptionLength} characters.");
        return trimmed;
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    public void SetRequirements(IEnumerable<JobRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var list = requirements.ToList();
        foreach (var r in list)
            ArgumentNullException.ThrowIfNull(r);
        if (HasDuplicateSkill(list))
            throw new DuplicateSkillException("Duplicate skill in requirements.");
        _requirements.Clear();
        _requirements.AddRange(list);
        Touch();
    }

    public void AddRequirement(JobRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (HasDuplicateSkill(_requirements.Append(requirement)))
            throw new DuplicateSkillException($"Skill '{requirement.Skill}' already exists in requirements.");
        _requirements.Add(requirement);
        Touch();
    }

    private static bool HasDuplicateSkill(IEnumerable<JobRequirement> requirements)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requirements)
        {
            if (!seen.Add(requirement.Skill.Name))
                return true;
        }
        return false;
    }

    public void SetLanguageRequirements(IEnumerable<LanguageRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var list = requirements.ToList();
        foreach (var r in list)
            ArgumentNullException.ThrowIfNull(r);
        if (HasDuplicateLanguage(list))
            throw new DuplicateSkillException("Duplicate language in requirements.");
        _languageRequirements.Clear();
        _languageRequirements.AddRange(list);
        Touch();
    }

    public void AddLanguageRequirement(LanguageRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (HasDuplicateLanguage(_languageRequirements.Append(requirement)))
            throw new DuplicateSkillException($"Language '{requirement.Name}' already exists in requirements.");
        _languageRequirements.Add(requirement);
        Touch();
    }

    // OrdinalIgnoreCase, exactly as HasDuplicateSkill compares skill names. LanguageRequirement stores
    // its name as typed, so record equality alone would let "English" and "english" both onto one
    // posting -- and PR 3 would then score the candidate against whichever it happened to read first.
    private static bool HasDuplicateLanguage(IEnumerable<LanguageRequirement> requirements)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requirements)
        {
            if (!seen.Add(requirement.Name))
                return true;
        }
        return false;
    }

    public void SetResponsibilities(IEnumerable<Responsibility> responsibilities)
    {
        ArgumentNullException.ThrowIfNull(responsibilities);
        var list = responsibilities.ToList();
        foreach (var r in list)
            ArgumentNullException.ThrowIfNull(r);
        _responsibilities.Clear();
        _responsibilities.AddRange(list);
        Touch();
    }

    public void AddResponsibility(Responsibility responsibility)
    {
        ArgumentNullException.ThrowIfNull(responsibility);
        _responsibilities.Add(responsibility);
        Touch();
    }

    public void Publish()
    {
        if (Status != JobPostingStatus.Draft)
            throw new InvalidJobPostingException("Only draft postings can be published.");
        Status = JobPostingStatus.Published;
        PublishedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void Close()
    {
        if (Status != JobPostingStatus.Published)
            throw new InvalidJobPostingException("Only published postings can be closed.");
        Status = JobPostingStatus.Closed;
        ClosesAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void Archive()
    {
        if (Status == JobPostingStatus.Archived)
            throw new InvalidJobPostingException("Posting is already archived.");
        Status = JobPostingStatus.Archived;
        Touch();
    }

    public override bool Equals(object? obj) => obj is JobPosting other && Id.Equals(other.Id);
    public override int GetHashCode() => Id.GetHashCode();
}
