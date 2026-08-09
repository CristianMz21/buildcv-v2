namespace BuildCv.Application.Common.Repositories;

using BuildCv.Application.Resumes;

/// <summary>
/// A stable identity for every entry in a resume's ten collections, aligned BY POSITION with the
/// aggregate's own lists: <c>For(Skills)[3]</c> identifies <c>resume.Skills[3]</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS AT ALL. The collections are owned VALUE objects — a <see cref="Domain.Resumes.Skill"/>
/// is its value and nothing more — so two entries a candidate can tell apart on screen may be
/// indistinguishable to the aggregate, and the <c>Remove*</c> methods match by equality or by name.
/// That is enough to delete "the skill called React" and not enough to delete "this award, the second
/// of two identical ones". The store already keeps a per-row surrogate key for exactly this; this type
/// is how it reaches a caller without putting the store's shape into the Domain.
/// </para>
/// <para>
/// THE ALIGNMENT IS THE CONTRACT, and it holds only within a single load. An adapter must build this
/// from the same materialization it returns the <see cref="Domain.Resumes.Resume"/> from — never from a
/// second query, whose ordering the first one does not promise. A handler therefore resolves an id to a
/// position and then works through the aggregate, and never caches a position across requests.
/// </para>
/// <para>
/// Ids are opaque to a client: they are unique within one resume, and nothing else about them is
/// promised. They are NOT guaranteed to be dense, ordered, or stable across a delete-and-re-add.
/// </para>
/// </remarks>
public sealed class ResumeItemIds
{
    private readonly IReadOnlyDictionary<ResumeSection, IReadOnlyList<int>> _bySection;

    public ResumeItemIds(IReadOnlyDictionary<ResumeSection, IReadOnlyList<int>> bySection)
    {
        ArgumentNullException.ThrowIfNull(bySection);
        _bySection = bySection;
    }

    /// <summary>
    /// The ids a resume with no entries has: none, in every section. Distinct from "not loaded" — a
    /// caller that has this has asked and been told the collections are empty.
    /// </summary>
    public static ResumeItemIds Empty { get; } =
        new(Enum.GetValues<ResumeSection>().ToDictionary(
            section => section, _ => (IReadOnlyList<int>)[]));

    /// <summary>The ids of one section, in the aggregate's own order.</summary>
    public IReadOnlyList<int> For(ResumeSection section) =>
        _bySection.TryGetValue(section, out var ids) ? ids : [];

    /// <summary>
    /// Where <paramref name="itemId"/> sits in <paramref name="section"/>, or null when this resume has
    /// no such entry. Null is the answer for an id belonging to a DIFFERENT resume too, which is what
    /// keeps a guessed id from reaching across accounts: the ids are only ever resolved against the
    /// aggregate the requester was already allowed to load.
    /// </summary>
    public int? PositionOf(ResumeSection section, int itemId)
    {
        var ids = For(section);

        for (var position = 0; position < ids.Count; position++)
        {
            if (ids[position] == itemId)
                return position;
        }

        return null;
    }
}
