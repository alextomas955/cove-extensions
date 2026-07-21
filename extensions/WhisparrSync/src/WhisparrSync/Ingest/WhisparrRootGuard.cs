using System.Security;
using WhisparrSync.State;

namespace WhisparrSync.Ingest;

/// <summary>
/// Defense-in-depth path containment: an attacker-influenceable imported path is handed to the
/// host's in-process ingest, so it is accepted ONLY when it canonicalizes inside a known Whisparr root at
/// a SEGMENT BOUNDARY. Pure and host-free so it is directly unit-testable. Fail-closed: an empty root set,
/// or a path that cannot be canonicalized, returns false.
/// </summary>
internal static class WhisparrRootGuard
{
    /// <summary>
    /// Whether <paramref name="path"/> resolves inside any of <paramref name="roots"/>. Both sides are
    /// canonicalized (<see cref="Path.GetFullPath(string)"/> to collapse <c>../</c>, then real-path resolution
    /// to follow symlinks — WR-02) and normalized with the shared <see cref="EventLedger.NormalizePath"/>, then
    /// compared on separator-terminated prefixes so a sibling like <c>/data/media-evil</c> does NOT match the
    /// root <c>/data/media</c>. A symlink inside a root whose target escapes the root is therefore rejected.
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
            // GetFullPath collapses any ../ traversal (purely LEXICAL — it does NOT follow symlinks), then
            // resolve the real on-disk path so a symlink inside a root whose target ESCAPES the root is caught
            // (WR-02). NormalizePath (separator-unify + trailing-trim, CASE-SENSITIVE, WR-01) makes the prefix
            // compare stable and the trailing-'/' segment boundary meaningful. Case-sensitive containment is
            // deliberate: on the Linux/Docker target /data/Media and /data/media are different directories, so a
            // differently-cased path must NOT match an allow-listed root.
            if (!TryResolveRealPath(Path.GetFullPath(value), out var real))
            {
                canonical = string.Empty;
                return false; // an existing entry whose real path we cannot resolve is rejected fail-closed
            }

            canonical = EventLedger.NormalizePath(real);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            canonical = string.Empty;
            return false; // an un-canonicalizable path is rejected fail-closed, never assumed safe
        }
    }

    // Best-effort resolve the REAL path behind a lexically-canonical <paramref name="fullPath"/>: Path.GetFullPath
    // is lexical only, so a symlink like /data/media/x -> /etc lets /data/media/x/passwd pass a lexical containment
    // check yet open a file outside the root. Resolve the containing directory's link target (follows an
    // intermediate symlink component) and the leaf's own link target, so the compare runs against where the path
    // ACTUALLY lands (WR-02). A not-yet-existing path has no link to follow, so its lexical form stands (the
    // coordinator's ingest still fails on a missing file); an EXISTING entry whose resolution throws is fail-closed.
    private static bool TryResolveRealPath(string fullPath, out string resolved)
    {
        resolved = fullPath;
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            var leaf = Path.GetFileName(fullPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                var directoryInfo = new DirectoryInfo(directory);
                var directoryTarget = directoryInfo.ResolveLinkTarget(returnFinalTarget: true);
                var realDirectory = directoryTarget is null
                    ? directoryInfo.FullName
                    : Path.GetFullPath(directoryTarget.FullName);
                resolved = string.IsNullOrEmpty(leaf) ? realDirectory : Path.GetFullPath(Path.Combine(realDirectory, leaf));
            }

            // If the (now directory-resolved) leaf is ITSELF a symlink, follow it to its final target too.
            if (File.Exists(resolved) || Directory.Exists(resolved))
            {
                FileSystemInfo leafInfo = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
                if (leafInfo.ResolveLinkTarget(returnFinalTarget: true) is { } leafTarget)
                {
                    resolved = Path.GetFullPath(leafTarget.FullName);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            resolved = string.Empty;
            return false; // an existing entry we cannot resolve → fail-closed, never assumed inside the root
        }
    }
}
