using static global::Renamer.Execution.PathOps;

namespace Renamer.Execution;

// The opt-in post-move step that deletes a source directory the move left empty. It is the one
// destructive directory write the renamer slice performs: only if empty, non-recursive, never a drive
// root, link-resolved, idempotent, and classifying instead of throwing.
//
// It runs only on the move success path, after the database save and the on-disk path assertion both
// pass, so a failed save that rolls the disk back never reaches a deletion that could not be undone. A
// cleanup failure comes back as a non-fatal warning, because the move succeeded and the database
// agrees.
//
// Deleting the emptied source folder means a later undo of that move skips the restore: UndoReplayer
// classifies a missing original directory as a skip and does not recreate it. The file is not lost; it
// stays at its verified destination with the database agreeing, and is simply not moved back.
public static class EmptySourceFolderCleaner
{
    // Deletes the directory only when it exists, holds no entry at all including untracked ones, is not
    // a drive root or a parentless path, and resolves to a real directory and not a link target. Any
    // other state is a no-op.
    //
    // A warning comes back when a guard refused or an IO or permission error interrupted the delete;
    // the warning is null when the directory was simply not eligible.
    public static (bool Removed, string? Warning) TryRemoveIfEmpty(string sourceDirFwd)
    {
        string native = ToNative(sourceDirFwd);

        // A racing second worker from the same folder, or the move's own source delete, may have
        // removed it already. An already-gone directory is a no-op and never a throw.
        if (!Directory.Exists(native))
        {
            return (false, null);
        }

        // A drive root or parentless path is never deleted: it reaches the whole volume and is never the
        // folder a file used to live in.
        if (IsRootOrParentless(native))
        {
            return (false, null);
        }

        // Resolve to the real on-disk target so a junction or symlink is not deleted as if it were the
        // empty directory it points at.
        string? resolved = ResolveCanonical(native);
        if (resolved is null)
        {
            return (false, "empty-folder cleanup skipped: source directory could not be resolved");
        }

        string deleteTarget = resolved;

        // The resolved target is re-checked because a junction can resolve to a drive root, which the
        // check on the unresolved path misses.
        if (IsRootOrParentless(deleteTarget))
        {
            return (false, null);
        }

        // A directory holding any entry, including an untracked file the batch never moved, is left
        // intact: deleting it would destroy data the move did not touch. A non-empty directory is the
        // common case and not an error.
        try
        {
            if (Directory.EnumerateFileSystemEntries(deleteTarget).Any())
            {
                return (false, null);
            }

            // A recursive delete would take whatever a racing writer dropped in between the empty check
            // and here, defeating that guard.
            Directory.Delete(deleteTarget, recursive: false);
            return (true, null);
        }
        catch (DirectoryNotFoundException)
        {
            // Raced to gone between the empty check and the delete, which is still a no-op.
            return (false, null);
        }
        catch (IOException ex)
        {
            // A racing writer re-populated it, or the directory is busy or locked. The move stands.
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
