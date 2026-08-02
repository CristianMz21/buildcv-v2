using System.Collections;

namespace BuildCv.Api.Contracts;

/// <summary>
/// A read-only view that applies <paramref name="project"/> to an element only when that element is
/// actually read. Backs the wire-contract-to-draft mapping in <see cref="ImportResumeRequest"/>.
/// </summary>
/// <remarks>
/// IT EXISTS TO STOP AN AMPLIFICATION, not to be elegant. Nothing in this repository sets Kestrel's
/// <c>MaxRequestBodySize</c> beyond the endpoint limit on <c>POST /resumes/import</c>, and minimal-API
/// binding deserializes the whole request before any BuildCv code runs. A <c>.Select(...).ToList()</c>
/// in <c>ToDraft()</c> then copied every element of all eleven collections a SECOND time — doubling the
/// object graph — before <c>ResumeDraftValidator.ForEachCapped</c> had even read <c>Count</c> to
/// discover the section was over its cap and would not be walked.
/// <para>
/// So <see cref="Count"/> is answered from the source and projects nothing. An over-cap section is now
/// refused having allocated exactly zero draft objects, which is what the cap claims to do.
/// </para>
/// <para>
/// The projection is applied on every read rather than memoized: each element is read once by the
/// validator's single walk, so caching would buy nothing and would reintroduce the second copy this
/// type exists to remove.
/// </para>
/// </remarks>
internal sealed class ProjectedList<TSource, TResult>(
    IReadOnlyList<TSource> source, Func<TSource, TResult> project) : IReadOnlyList<TResult>
{
    public int Count => source.Count;

    public TResult this[int index] => project(source[index]);

    public IEnumerator<TResult> GetEnumerator()
    {
        for (var index = 0; index < source.Count; index++)
            yield return project(source[index]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class ProjectedList
{
    public static IReadOnlyList<TResult>? OrNull<TSource, TResult>(
        IReadOnlyList<TSource>? source, Func<TSource, TResult> project) =>
        source is null ? null : new ProjectedList<TSource, TResult>(source, project);
}
