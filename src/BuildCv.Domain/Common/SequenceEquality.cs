namespace BuildCv.Domain.Common;

/// <summary>
/// Sequence comparison for the ordered list members of the CV item records.
/// </summary>
/// <remarks>
/// C# record equality compares a member with <c>EqualityComparer&lt;T&gt;.Default</c>, which for an
/// <c>IReadOnlyList&lt;T&gt;</c> member is REFERENCE equality: two lists holding the same contents are
/// not equal unless they are the same instance. Within one import the entries share their lists by
/// <c>with { }</c>; across two imports they are always different instances, and the profile's
/// idempotent <c>Add</c> would then hold the same job twice. The four item records that carry such a
/// member — <c>Experience</c>, <c>Project</c>, <c>Skill</c> and <c>Interest</c> — therefore override
/// <c>Equals</c>/<c>GetHashCode</c> to compare those members BY SEQUENCE. These two methods are the
/// single statement of what "by sequence" means; a record that needs a different ordering must not
/// hand-roll a third copy of the loop.
/// </remarks>
internal static class SequenceEquality
{
    public static bool Equal<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
                return false;
        }

        return true;
    }

    public static int Hash<T>(IReadOnlyList<T>? list)
    {
        if (list is null)
            return 0;

        var hash = new HashCode();
        foreach (var item in list)
            hash.Add(item);
        return hash.ToHashCode();
    }
}
