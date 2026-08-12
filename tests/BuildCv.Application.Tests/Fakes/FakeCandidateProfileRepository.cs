namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Identity;

// The counterpart of FakeResumeRepository, for the use case that writes the profile as well as the
// resume: importing a CV. Counts reads and writes the same way, so the handler tests can assert that a
// rejected draft reaches NEITHER store and a successful one writes each store exactly once.
public sealed class FakeCandidateProfileRepository : ICandidateProfileRepository
{
    private readonly List<CandidateProfile> _profiles = [];

    public int ReadCount { get; private set; }
    public int AddCount { get; private set; }
    public int WriteCount { get; private set; }

    public Task<CandidateProfile?> GetByOwnerIdAsync(
        AccountId ownerId, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(_profiles.FirstOrDefault(profile => profile.OwnerId == ownerId));
    }

    // Ids by position, exactly as this suite's resume fake does — see FakeResumeRepository for why that
    // is a simplification rather than a behaviour either real store has. Nothing in the import tests
    // resolves an id, so this exists to honour the port rather than to be exercised.
    public Task<CandidateProfileWithItemIds?> GetByOwnerIdWithItemIdsAsync(
        AccountId ownerId, CancellationToken cancellationToken = default)
    {
        ReadCount++;

        var profile = _profiles.FirstOrDefault(p => p.OwnerId == ownerId);
        if (profile is null)
            return Task.FromResult<CandidateProfileWithItemIds?>(null);

        static IReadOnlyList<int> Positions(int count) => [.. Enumerable.Range(1, count)];

        return Task.FromResult<CandidateProfileWithItemIds?>(new CandidateProfileWithItemIds(
            profile,
            new ResumeItemIds(new Dictionary<ResumeSection, IReadOnlyList<int>>
            {
                [ResumeSection.Experiences] = Positions(profile.Experiences.Count),
                [ResumeSection.Educations] = Positions(profile.Educations.Count),
                [ResumeSection.Skills] = Positions(profile.Skills.Count),
                [ResumeSection.Projects] = Positions(profile.Projects.Count),
                [ResumeSection.Certificates] = Positions(profile.Certificates.Count),
                [ResumeSection.Languages] = Positions(profile.Languages.Count),
                [ResumeSection.Awards] = Positions(profile.Awards.Count),
                [ResumeSection.Publications] = Positions(profile.Publications.Count),
                [ResumeSection.Interests] = Positions(profile.Interests.Count),
                [ResumeSection.References] = Positions(profile.References.Count)
            })));
    }

    public Task AddAsync(CandidateProfile profile, CancellationToken cancellationToken = default)
    {
        AddCount++;
        WriteCount++;
        _profiles.Add(profile);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CandidateProfile profile, CancellationToken cancellationToken = default)
    {
        WriteCount++;
        var index = _profiles.FindIndex(p => p.OwnerId == profile.OwnerId);
        if (index >= 0)
            _profiles[index] = profile;
        return Task.CompletedTask;
    }
}
