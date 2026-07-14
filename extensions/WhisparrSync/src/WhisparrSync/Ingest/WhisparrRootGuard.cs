using WhisparrSync.State;

namespace WhisparrSync.Ingest;

/// <summary>
/// Defense-in-depth path containment (T-03-PT): an attacker-influenceable imported path is handed to the
/// host's in-process ingest, so it is accepted ONLY when it canonicalizes inside a known Whisparr root at
/// a SEGMENT BOUNDARY. Pure and host-free so it is directly unit-testable. Fail-closed: an empty root set,
/// or a path that cannot be canonicalized, returns false.
/// </summary>
internal static class WhisparrRootGuard
{
    /// <summary>
    /// Whether <paramref name="path"/> resolves inside any of <paramref name="roots"/>. Both sides are
    /// canonicalized (<see cref="Path.GetFullPath(string)"/>) and normalized with the shared
    /// <see cref="EventLedger.NormalizePath"/>, then compared on separator-terminated prefixes so a sibling
    /// like <c>/data/media-evil</c> does NOT match the root <c>/data/media</c>.
    /// </summary>
    public static bool IsWithinAnyRoot(string path, IReadOnlyCollection<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || roots is null || roots.Count == 0)
        {
            return false;
        }

        if (!TryCanonicalize(path, out var canonicalPath))
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !TryCanonicalize(root, out var canonicalRoot))
            {
                continue;
            }

            if (canonicalPath == canonicalRoot ||
                canonicalPath.StartsWith(canonicalRoot + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCanonicalize(string value, out string canonical)
    {
        try
        {
            // NormalizePath (separator-unify + case-fold + trailing-trim) makes the prefix compare stable
            // and the trailing-'/' segment boundary meaningful; GetFullPath collapses any ../ traversal first.
            canonical = EventLedger.NormalizePath(Path.GetFullPath(value));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            canonical = string.Empty;
            return false; // an un-canonicalizable path is rejected fail-closed, never assumed safe
        }
    }
}
