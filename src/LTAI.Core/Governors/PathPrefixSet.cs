using System.Collections;

namespace LTAI.Core.Governors;

/// <summary>
/// O(log n) prefix-match set for sandbox path validation.
/// Replaces O(n) linear scan over HashSet.AllowedPaths/BlockedPaths.
/// </summary>
public sealed class PathPrefixSet : IReadOnlyCollection<string>
{
    private readonly string[] _sorted;
    private readonly StringComparison _comparison;

    public PathPrefixSet(IEnumerable<string> paths, StringComparer? comparer = null)
    {
        var c = comparer ?? StringComparer.OrdinalIgnoreCase;
        _comparison = c == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase
            : c == StringComparer.Ordinal ? StringComparison.Ordinal
            : c == StringComparer.InvariantCultureIgnoreCase ? StringComparison.InvariantCultureIgnoreCase
            : StringComparison.InvariantCulture;
        _sorted = paths
            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(c)
            .OrderBy(p => p, c)
            .ToArray();
    }

    public int Count => _sorted.Length;

    /// <summary>
    /// Returns true if <paramref name="fullPath"/> starts with any path in the set.
    /// Uses binary search to find the candidate range, then checks StartsWith.
    /// O(log n + k) where k is the small number of path-length collisions.
    /// </summary>
    public bool ContainsPrefix(string fullPath)
    {
        if (_sorted.Length == 0) return false;

        // Binary search: find the first entry that could be a prefix.
        // Since paths are sorted lexicographically, a prefix P of fullPath F
        // must satisfy P <= F (because F = P + suffix, and suffix >= "").
        var lo = 0;
        var hi = _sorted.Length - 1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var cmp = string.Compare(_sorted[mid], fullPath, _comparison);

            if (cmp == 0) return true; // exact match
            if (cmp < 0)
            {
                // _sorted[mid] < fullPath — check if it's a prefix
                if (fullPath.StartsWith(_sorted[mid], _comparison))
                {
                    // Also ensure the next char is a directory separator (not a partial name match)
                    if (fullPath.Length > _sorted[mid].Length &&
                        (fullPath[_sorted[mid].Length] == Path.DirectorySeparatorChar ||
                         fullPath[_sorted[mid].Length] == Path.AltDirectorySeparatorChar))
                        return true;
                }
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Fallback: check the nearest candidate (the last entry where _sorted[i] < fullPath)
        if (hi >= 0 && fullPath.StartsWith(_sorted[hi], _comparison))
        {
            if (fullPath.Length > _sorted[hi].Length &&
                (fullPath[_sorted[hi].Length] == Path.DirectorySeparatorChar ||
                 fullPath[_sorted[hi].Length] == Path.AltDirectorySeparatorChar))
                return true;
        }

        return false;
    }

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_sorted).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _sorted.GetEnumerator();
}
