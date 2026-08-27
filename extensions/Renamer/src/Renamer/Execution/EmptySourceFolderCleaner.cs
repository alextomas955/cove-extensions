using static global::Renamer.Execution.PathOps;

namespace Renamer.Execution;

/// <summary>
/// The opt-in post-move step that deletes a source directory the move left empty. It is the one
/// destructive directory write the renamer slice performs, so it inherits the mover's safety discipline:
/// only-if-empty, non-recursive, never a drive root, link-resolved, idempotent, and
/// classify-not-throw.
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
    /// or a parentless path, and resolves to a real directory rather than a junction/symlink target. Any
    /// other state is a no-op.
    /// </summary>
    /// <param name="sourceDirFwd">The former source directory (forward-slash) the moved file left behind.</param>
    /// <returns>
    /// <c>removed=true</c> with no warning when the directory was deleted; otherwise <c>removed=false</c>
    /// with a warning when a guard refused or an IO/permission error interrupted the delete, or a null
    /// warning when the directory was simply not eligible (non-empty, a root, or already gone).
    /// </returns>
    public static (bool Removed, string? Warning) TryRemoveIfEmpty(string sourceDirFwd)
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
        // empty directory it points at.
        string? resolved = ResolveCanonical(native);
        if (resolved is null)
        {
            return (false, "empty-folder cleanup skipped: source directory could not be resolved");
        }

        string deleteTarget = resolved;

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


}
