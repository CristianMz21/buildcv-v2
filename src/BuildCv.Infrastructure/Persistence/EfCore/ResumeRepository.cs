using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence.EfCore;

// Resumes, against SQL Server.
//
// No Include calls anywhere below. Every child collection on a resume is an OWNED type, and owned
// navigations are loaded with their principal by default — an Include here would be a no-op that reads
// like a requirement, and the next contributor would copy it onto a real navigation that needs one.
// ResumeRepositoryTests loads a fully populated resume through this repository and asserts the ten
// collections arrive, so the guarantee is pinned rather than assumed.
internal sealed class ResumeRepository : IResumeRepository
{
    private readonly BuildCvDbContext _context;

    public ResumeRepository(BuildCvDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<Resume?> GetByIdAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return await _context.Resumes.AsTracking().FirstOrDefaultAsync(resume => resume.Id == id, cancellationToken);
    }

    // A pure read: nothing mutates a list, so it rides the context-wide NoTracking default. Ordered
    // newest-first on Seq, which is the direction the (OwnerId, Seq DESC) index is laid out in and the
    // order the "my resumes" screen asks for. CreatedAt would not do — two resumes can share a
    // timestamp, and a non-total order makes keyset pagination skip or repeat rows.
    public async Task<IReadOnlyList<Resume>> GetByOwnerIdAsync(
        AccountId ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);

        return await _context.Resumes
            .Where(resume => resume.OwnerId == ownerId)
            .OrderByDescending(resume => EF.Property<long>(resume, ShadowColumns.Seq))
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resume);
        _context.Resumes.Add(resume);
        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    public async Task UpdateAsync(Resume resume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resume);

        // See AccountRepository.UpdateAsync: a detached root carries no rowversion, so this path reports
        // a concurrency conflict rather than overwriting blind.
        if (_context.Entry(resume).State is EntityState.Detached)
            _context.Resumes.Update(resume);

        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    // Resume has no domain-level Delete(), so unlike Account and Organization there is no status to
    // carry: Remove() through AuditSaveChangesInterceptor IS the delete. The interceptor converts it to
    // a tombstone and returns the ten cascaded owned collections to Unchanged, so the whole aggregate
    // survives on disk and disappears from every unfiltered read.
    public async Task DeleteAsync(ResumeId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        var resume = await _context.Resumes.AsTracking()
            .FirstOrDefaultAsync(entity => entity.Id == id, cancellationToken);
        if (resume is null)
            return;

        _context.Resumes.Remove(resume);
        await CascadeToAnalysesAsync(id, cancellationToken);

        await _context.SaveTranslatingFailuresAsync(cancellationToken);
    }

    // DECISION: deleting a resume tombstones the analyses derived from it, in the same unit of work.
    //
    // Analysis holds a ResumeId but no foreign key to Resumes — the two are separate aggregates — so
    // neither the database nor the query filter on Resumes reaches them. Left alone, "delete my resume"
    // would hide the resume while every score ever computed from it stayed fully readable and joinable
    // by ResumeId, indefinitely. That is a privacy answer, not a referential-integrity detail, and the
    // user-visible promise has to mean one thing.
    //
    // The alternative considered and rejected: keep the analyses as de-identified derived data. They do
    // hold no names — every column is a score, a weight or generated advice — but they are keyed to the
    // resume by an id that survives, so "de-identified" would only be true until someone joined the two
    // with IgnoreQueryFilters(). Retention for analytics is a purge-policy decision to make deliberately,
    // not something to inherit from a missing FK.
    //
    // A cross-aggregate write from a repository is the cost, and it is taken knowingly: there is no FK
    // for the engine to cascade through, and putting this in a handler would mean every future caller of
    // DeleteAsync has to remember it.
    private async Task CascadeToAnalysesAsync(ResumeId resumeId, CancellationToken cancellationToken)
    {
        var analyses = await _context.Analyses.AsTracking()
            .Where(analysis => analysis.ResumeId == resumeId)
            .ToListAsync(cancellationToken);

        if (analyses.Count > 0)
            _context.Analyses.RemoveRange(analyses);
    }
}
