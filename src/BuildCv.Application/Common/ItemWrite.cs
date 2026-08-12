namespace BuildCv.Application.Common;

using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;

/// <summary>
/// The write every "put an entry into one of a CV-shaped aggregate's ten collections" use case
/// performs, whether it is appending a new entry or replacing an existing one.
/// </summary>
/// <remarks>
/// <para>
/// ONE COPY OF THE PLUMBING, for the reason spelled out on
/// <see cref="RemoveResumeItemHandler"/>: the ten Add handlers of one aggregate differed in exactly one
/// expression — the two lines that build the value and hand it to the aggregate — and were otherwise
/// the same load, the same ownership check and the same two catch blocks, written ten times. Two
/// aggregates share that shape, so a second copy of this core would put the same rule in two places
/// that drift on the first change to either. The next aggregate that owns these collections must route
/// through this core, not copy it.
/// <see cref="Domain.Resumes.Resume"/> and <see cref="Domain.Candidates.CandidateProfile"/> key their
/// ten collections by the SAME <see cref="ResumeSection"/> enum and by position, and both use
/// <see cref="ResumeItemIds"/> for identity — the shared shape is what makes one core possible. The
/// per-aggregate part stays in the per-aggregate wrapper, where it belongs.
/// </para>
/// <para>
/// THE VALUE IS BUILT BEFORE ANYTHING IS LOADED, which is what <c>Func&lt;Action&lt;T&gt;&gt;</c>
/// buys and a plain <c>Action&lt;T&gt;</c> would not: a lambda that both constructs and appends runs
/// its constructor at append time, which on a replace is AFTER the entry it replaces has been removed.
/// That matters because the in-memory stores hand out the stored instance itself
/// (<c>InMemoryResumeRepository.GetByIdAsync</c> returns <c>row.Item</c>), so a mutation that is never
/// saved is still a mutation there — and the whole Api suite runs on those stores. A rejected value
/// would have deleted the entry the caller was trying to fix.
/// </para>
/// <para>
/// A REPLACE REMOVES BEFORE IT ADDS, and both happen inside one save. Four of these collections refuse
/// an entry whose name duplicates one already there, so adding first would refuse every edit that
/// changes something OTHER than the name — the common case. The order also cannot be left to the client
/// as delete-then-post: a post that fails after a successful delete loses what the candidate wrote,
/// which is the whole reason this route exists rather than two.
/// </para>
/// <para>
/// THE OWNERSHIP CHECK RUNS BEFORE ANY POSITION IS RESOLVED, and a missing entry is
/// <c>"{section} entry not found."</c>, never "Forbidden.", resolved only against an aggregate the
/// caller was already allowed to load. Ids are unique within one aggregate, so aiming a valid id at
/// somebody else's teaches the caller nothing.
/// </para>
/// </remarks>
internal static class ItemWrite
{
    /// <param name="load">
    /// Loads the aggregate the request addresses — whole but without ids for an append, with
    /// <see cref="ResumeItemIds"/> for a replace — and returns whichever shape the operation needs. The
    /// tuple element types make the two shapes explicit: an append's delegate returns
    /// <c>(aggregate, null)</c>, a replace's returns <c>(aggregate, ids)</c>.
    /// </param>
    /// <param name="ownerIdOf">The account that owns the aggregate — the subject of the ownership check.</param>
    /// <param name="removeAt">Removes the entry at a resolved position of a section, in aggregate order.</param>
    /// <param name="save">The repository write — the single <c>UpdateAsync</c> a replace wraps its remove and add in.</param>
    /// <param name="requesterId">The account making the request.</param>
    /// <param name="section">Which of the aggregate's ten collections is being addressed.</param>
    /// <param name="replacingItemId">
    /// The id of the entry to replace, or null for an append. The fork that decides which load shape
    /// the operation needs and whether a position is resolved at all.
    /// </param>
    /// <param name="notFoundMessage">
    /// What the aggregate is called when it cannot be loaded — <c>"Resume not found."</c> for a resume,
    /// <c>"Profile not found."</c> for a profile. It must end in "not found." so the Api's
    /// <c>ToHttpResult</c> maps it to a 404.
    /// </param>
    public static async Task<Result<T>> Execute<T>(
        Func<CancellationToken, Task<(T? Aggregate, ResumeItemIds? ItemIds)>> load,
        Func<T, AccountId> ownerIdOf,
        Action<T, ResumeSection, int> removeAt,
        Func<T, CancellationToken, Task> save,
        AccountId requesterId,
        ResumeSection section,
        int? replacingItemId,
        string notFoundMessage,
        Func<Action<T>> build,
        CancellationToken cancellationToken)
    {
        try
        {
            var append = build();

            // An append needs no ids, and the with-ids load exists precisely so that the paths which do
            // not address an entry are spared the per-entry id walk. See GetByIdWithItemIdsAsync.
            if (replacingItemId is null)
            {
                var loaded = await load(cancellationToken);
                if (loaded.Aggregate is null)
                    return Result<T>.Failure(notFoundMessage);

                if (ownerIdOf(loaded.Aggregate) != requesterId)
                    return Result<T>.Failure("Forbidden.");

                append(loaded.Aggregate);
                await save(loaded.Aggregate, cancellationToken);
                return Result<T>.Success(loaded.Aggregate);
            }

            var withIds = await load(cancellationToken);
            if (withIds.Aggregate is null || withIds.ItemIds is null)
                return Result<T>.Failure(notFoundMessage);

            if (ownerIdOf(withIds.Aggregate) != requesterId)
                return Result<T>.Failure("Forbidden.");

            // "not found", never "forbidden", and resolved only against an aggregate the caller was
            // already allowed to load — the same ordering RemoveResumeItemHandler documents. Ids are
            // unique within one aggregate, so aiming a valid id at somebody else's teaches the caller
            // nothing.
            var position = withIds.ItemIds.PositionOf(section, replacingItemId.Value);
            if (position is null)
                return Result<T>.Failure($"{section} entry not found.");

            removeAt(withIds.Aggregate, section, position.Value);
            append(withIds.Aggregate);

            await save(withIds.Aggregate, cancellationToken);
            return Result<T>.Success(withIds.Aggregate);
        }
        catch (DomainException ex)
        {
            return Result<T>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<T>.Failure(ex.Message);
        }
    }
}
