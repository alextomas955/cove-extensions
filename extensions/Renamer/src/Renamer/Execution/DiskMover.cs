namespace Renamer.Execution;

// The same-volume disk tier of the executor: a classifying wrapper over System.IO.File. It performs
// the primary file move and the sidecar moves, classifies a failure into a result without throwing
// out, and offers a best-effort rollback that reverses a successful move so the disk can be restored
// when a later database save fails.
//
// Safety contract:
// - The move takes the two-argument File.Move overload and never overwrite:true, so an existing
//   destination is never clobbered.
// - A locked source or a permission failure is caught and reported. Whatever process holds the lock is
//   never touched: this class references no OS process API.
// - A sidecar whose target already exists is left untouched and recorded as a warning.
public static class DiskMover
{
    // Moves the primary file, creating the destination directory when needed, then moves each planned
    // sidecar without clobbering. A locked source returns Locked and an occupied destination returns
    // TargetExists; the atomic move raises one IOException for both, so they are told apart by testing
    // the destination. In either case the primary move did not happen and no sidecar is touched. A
    // permission failure returns PermissionDenied.
    //
    // Those four are the only MoveOutcome members this tier produces. An atomic rename has no copy to
    // read back and no cancellation point, so VerifyFailed and Cancelled belong to CrossVolumeMover.
    public static MoveResult Move(string oldFull, string newFull, IReadOnlyList<SidecarMove>? sidecars = null)
    {
        try
        {
            Movers.EnsureParentDir(newFull);
            // The two-argument overload throws IOException when the destination exists and when the
            // source is locked.
            System.IO.File.Move(oldFull, newFull);
        }
        catch (IOException ex)
        {
            // The exception cannot say which cause it carries, so the destination is tested. The
            // message is never read; see MoveOutcome.TargetExists.
            return System.IO.File.Exists(newFull)
                ? new MoveResult(false, MoveOutcome.TargetExists, [], [], $"target exists, not overwritten: {ex.Message}")
                : new MoveResult(false, MoveOutcome.Locked, [], [], $"source locked/in-use: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new MoveResult(false, MoveOutcome.PermissionDenied, [], [], $"permission denied: {ex.Message}");
        }

        var moved = new List<SidecarMove>();
        var warnings = new List<string>();
        if (sidecars is not null)
        {
            foreach (var sc in sidecars)
            {
                if (System.IO.File.Exists(sc.To))
                {
                    // The pre-existing target is left untouched.
                    warnings.Add($"sidecar target exists, skipped: {sc.To}");
                    continue;
                }

                try
                {
                    Movers.EnsureParentDir(sc.To);
                    System.IO.File.Move(sc.From, sc.To);
                    moved.Add(sc);
                }
                catch (IOException ex)
                {
                    // A locked sidecar is non-fatal; the primary file has already moved.
                    warnings.Add($"sidecar move failed (locked/exists), skipped: {sc.From} -> {sc.To}: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    warnings.Add($"sidecar move failed (permission), skipped: {sc.From} -> {sc.To}: {ex.Message}");
                }
            }
        }

        return new MoveResult(true, MoveOutcome.Moved, moved, warnings, null);
    }

    // Reverses a successful Move: the primary file goes back to oldFull and every moved sidecar back to
    // its source. Best-effort, so a secondary failure such as the old slot being re-occupied is
    // reported in the returned warnings and never thrown, and a failed save's cleanup cannot crash the
    // batch. The warnings are empty when the restore was clean.
    public static IReadOnlyList<string> Rollback(string oldFull, string newFull, IReadOnlyList<SidecarMove> movedSidecars)
    {
        var warnings = new List<string>();

        // Sidecars go back in reverse move order, then the primary file.
        for (int i = movedSidecars.Count - 1; i >= 0; i--)
        {
            var sc = movedSidecars[i];
            SafeMoveBack(sc.To, sc.From, warnings);
        }

        SafeMoveBack(newFull, oldFull, warnings);
        return warnings;
    }

    // Records a failure as a warning and never throws.
    private static void SafeMoveBack(string from, string to, List<string> warnings)
    {
        try
        {
            if (!System.IO.File.Exists(from))
            {
                warnings.Add($"rollback source missing, cannot restore: {from}");
                return;
            }
            if (System.IO.File.Exists(to))
            {
                warnings.Add($"rollback target re-occupied, leaving as-is: {to}");
                return;
            }
            Movers.EnsureParentDir(to);
            System.IO.File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"rollback move failed {from} -> {to}: {ex.Message}");
        }
    }
}
