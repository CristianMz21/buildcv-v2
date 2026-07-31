using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Organizations;

namespace BuildCv.Domain.Jobs;

public sealed class JobPosting
{
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 5000;

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
    public IReadOnlyList<JobRequirement> Requirements { get; private set; }
    public IReadOnlyList<Responsibility> Responsibilities { get; private set; }

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
        Requirements = [];
        Responsibilities = [];
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
        Requirements = list.AsReadOnly();
        Touch();
    }

    public void AddRequirement(JobRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (HasDuplicateSkill(Requirements.Append(requirement)))
            throw new DuplicateSkillException($"Skill '{requirement.Skill}' already exists in requirements.");
        Requirements = [.. Requirements, requirement];
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

    public void SetResponsibilities(IEnumerable<Responsibility> responsibilities)
    {
        ArgumentNullException.ThrowIfNull(responsibilities);
        var list = responsibilities.ToList();
        foreach (var r in list)
            ArgumentNullException.ThrowIfNull(r);
        Responsibilities = list.AsReadOnly();
        Touch();
    }

    public void AddResponsibility(Responsibility responsibility)
    {
        ArgumentNullException.ThrowIfNull(responsibility);
        Responsibilities = [.. Responsibilities, responsibility];
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
