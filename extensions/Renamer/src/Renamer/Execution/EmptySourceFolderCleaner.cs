namespace Renamer.Execution;

/// <summary>
/// The opt-in post-move step that deletes a source directory the move left empty. It is the one
/// destructive directory write the renamer slice performs, so it inherits the mover's safety discipline:
/// only-if-empty, non-recursive, never a drive root or a configured allowed root, confined to the
/// allowlist, link-resolved, idempotent, and classify-not-throw.
///
/// <para>
/// It runs ONLY on the move success path (after the DB save + on-disk path assertion both pass), so a
/// failed save — which rolls the disk back — never reaches a deletion that could not be undone. A
/// cleanup failure is returned as a non-fatal warning, never thrown: the move already succeeded and the
/// DB agrees, so a non-deletable empty directory must not flip a moved item to failed.
/// </para>
///
/// <para>
/// Undo interaction: deleting the emptied source folder means a later undo of that move SKIPS the
/// restore — <see cref="UndoReplayer"/> classifies a missing original directory as a skip (it checks
/// the old directory still exists before the restore and does not recreate it). The file is never lost
/// (the DB stays authoritative and the file remains at its verified destination); it simply is not
/// moved back into the now-gone folder.
/// </para>
/// </summary>
public static class EmptySourceFolderCleaner
{
    /// <summary>
    /// Deletes <paramref name="sourceDirFwd"/> when, and only when, it is safe to: the directory exists,
    /// is completely empty (no files, no subdirectories — untracked entries count), is not a drive root
    /// or a parentless path, is confined to <paramref name="allowedRoots"/> (when any are configured)
    /// and is not itself a configured allowed root, and resolves to a real directory rather than a
    /// junction/symlink target. Any other state is a no-op.
    /// </summary>
    /// <param name="sourceDirFwd">The former source directory (forward-slash) the moved file left behind.</param>
    /// <param name="allowedRoots">The owner-configured write boundary; empty = legacy source-confine (no allowlist).</param>
    /// <returns>
    /// <c>removed=true</c> with no warning when the directory was deleted; otherwise <c>removed=false</c>
    /// with a warning when a guard refused or an IO/permission error interrupted the delete, or a null
    /// warning when the directory was simply not eligible (non-empty, a root, or already gone).
    /// </returns>
    public static (bool Removed, string? Warning) TryRemoveIfEmpty(string sourceDirFwd, IReadOnlyList<string> allowedRoots)
    {
        string native = ToNative(sourceDirFwd);

        // Idempotent: a racing second worker from the same folder, or the move's own delete-source,
        // may have already removed it. An already-gone directory is the success-noop, never a throw.
        if (!Directory.Exists(native))
        {
            return (false, null);
        }

        // Never a drive root or a parentless path: deleting one has whole-volume blast radius and can
        // never be "the folder a file used to live in".
        if (IsRootOrParentless(native))
        {
            return (false, null);
        }

        // Resolve to the real on-disk target so a junction/symlink is not deleted as if it were the
        // empty directory it points at. With a configured allowlist, the same CanonicalPathGuard.Check
        // the move's destination passed also confines (and link-resolves) the delete target; the
        // resolved target it returns is the path actually deleted.
        string deleteTarget;
        if (allowedRoots.Count > 0)
        {
            var guard = CanonicalPathGuard.Check(sourceDirFwd, allowedRoots);
            if (!guard.Accepted)
            {
                return (false, $"empty-folder cleanup skipped: {guard.Reason}");
            }

            if (ResolvesToAnyRoot(guard.ResolvedTarget, allowedRoots))
            {
                return (false, "empty-folder cleanup skipped: directory is a configured allowed root");
            }

            deleteTarget = ToNative(guard.ResolvedTarget);
        }
        else
        {
            string? resolved = ResolveCanonical(native);
            if (resolved is null)
            {
                return (false, "empty-folder cleanup skipped: source directory could not be resolved");
            }

            deleteTarget = resolved;
        }

        // Re-check root-ness on the RESOLVED target, not just the pre-resolution path: a junction
        // could resolve to a drive root, which the earlier check on the unresolved path would miss.
        // Directory.Delete on a root throws anyway, but a deletion feature should refuse a volume by
        // policy rather than rely on the OS to reject it.
        if (IsRootOrParentless(deleteTarget))
        {
            return (false, null);
        }

        // Only-if-empty: a single enumerate. A directory that still holds ANY entry (including an
        // untracked file the batch never moved) is left intact — deleting it would destroy data the
        // move did not touch. A non-empty directory is the expected common case, not an error.
        try
        {
            if (Directory.EnumerateFileSystemEntries(deleteTarget).Any())
            {
                return (false, null);
            }

            // Non-recursive only: recursive:true would delete whatever a racing writer dropped in
            // between the empty-check and here, defeating the only-if-empty guard.
            Directory.Delete(deleteTarget, recursive: false);
            return (true, null);
        }
        catch (DirectoryNotFoundException)
        {
            // Raced to gone between the empty-check and the delete → still the success-noop.
            return (false, null);
        }
        catch (IOException ex)
        {
            // A racing writer re-populated it, or the directory is otherwise busy/locked. Surface a
            // warning; the move stands.
            return (false, $"empty-folder cleanup skipped: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, $"empty-folder cleanup skipped: {ex.Message}");
        }
    }

    private static bool IsRootOrParentless(string nativeDir)
    {
        string? parent = Path.GetDirectoryName(nativeDir);
        if (string.IsNullOrEmpty(parent))
        {
            return true;
        }

        string? root = Path.GetPathRoot(nativeDir);
        return !string.IsNullOrEmpty(root)
            && string.Equals(
                NormalizeSlash(nativeDir).TrimEnd('/'),
                NormalizeSlash(root).TrimEnd('/'),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // Canonicalizes each allowed root the SAME way the resolved target is canonicalized (Check resolves
    // a root passed as its own sole allowlist back through the identical link-/8.3-resolution), so the
    // "is this dir a configured root" compare is real-target vs real-target.
    private static bool ResolvesToAnyRoot(string resolvedTargetFwd, IReadOnlyList<string> allowedRoots)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string target = NormalizeSlash(resolvedTargetFwd).TrimEnd('/');
        foreach (var root in allowedRoots)
        {
            var rootGuard = CanonicalPathGuard.Check(root, [root]);
            string canonicalRoot = (rootGuard.Accepted ? rootGuard.ResolvedTarget : NormalizeSlash(root)).TrimEnd('/');
            if (string.Equals(target, canonicalRoot, cmp))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ResolveCanonical(string nativeDir)
    {
        try
        {
            var link = Directory.ResolveLinkTarget(nativeDir, returnFinalTarget: true);
            return link?.FullName ?? nativeDir;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ToNative(string p) => p.Replace('/', Path.DirectorySeparatorChar);

    private static string NormalizeSlash(string p) => p.Replace('\\', '/');
}
