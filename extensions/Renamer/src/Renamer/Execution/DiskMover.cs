namespace Renamer.Execution;

/// <summary>
/// The host-free disk tier of the executor: a thin, classifying wrapper over
/// <see cref="System.IO.File"/> that the <c>RenamerExecutor</c> composes. It performs the primary
/// file move and the sidecar caption moves, classifies failures (locked / target-exists /
/// permission-denied) into a result <em>without throwing out</em>, and exposes a best-effort
/// rollback that reverses a successful move (and any moved sidecars) so the disk can be restored
/// when a subsequent DB save fails.
///
/// SAFETY CONTRACT:
/// <list type="bullet">
/// <item>Uses the 2-arg <see cref="System.IO.File.Move(string,string)"/> overload — NEVER
/// <c>overwrite:true</c>. Clobbering an existing destination would lose data, so it must never happen.</item>
/// <item>A locked source (<see cref="IOException"/>, e.g. Windows ERROR_SHARING_VIOLATION) or a
/// permission failure (<see cref="UnauthorizedAccessException"/>) is caught and reported — whatever
/// process holds the lock is NEVER touched. This class references no OS process API (no killing,
/// no closing of foreign handles).</item>
/// <item>Sidecars are skip-not-clobber: an existing sidecar target is left untouched and a warning
/// is recorded.</item>
/// </list>
/// Pure <see cref="System.IO"/> — no <c>CoveContext</c>/EF dependency — so it is testable purely
/// against a real temp directory.
/// </summary>
public sealed class DiskMover
{
    /// <summary>One planned sidecar move: absolute source → absolute destination (forward/native slashes ok).</summary>
    public readonly record struct SidecarMove(string From, string To);

    /// <summary>
    /// The outcome of a <see cref="Move"/>: whether the primary file moved, the sidecars that were
    /// actually moved (for rollback), any skip warnings, and a classification + reason when the
    /// primary move did not happen. A non-<see cref="Moved"/> result is a SKIP, never a thrown error.
    /// </summary>
    /// <param name="Moved">True iff the primary file was moved old→new.</param>
    /// <param name="Outcome">The classification of the primary move attempt.</param>
    /// <param name="MovedSidecars">The sidecar pairs that actually moved (in move order) — what rollback reverses.</param>
    /// <param name="Warnings">Non-fatal notes (e.g. a skipped sidecar whose target already existed).</param>
    /// <param name="Reason">A human-readable reason when the primary move was skipped; null on success.</param>
    public sealed record MoveResult(
        bool Moved,
        MoveOutcome Outcome,
        IReadOnlyList<SidecarMove> MovedSidecars,
        IReadOnlyList<string> Warnings,
        string? Reason);

    /// <summary>How a primary move attempt was classified.</summary>
    public enum MoveOutcome
    {
        /// <summary>The file moved old→new successfully.</summary>
        Moved,

        /// <summary>The source was locked/in-use OR the destination already existed.</summary>
        LockedOrExists,

        /// <summary>The OS denied permission for the move.</summary>
        PermissionDenied,
    }

    /// <summary>
    /// Moves <paramref name="oldFull"/> → <paramref name="newFull"/> (creating the destination
    /// directory if needed), then moves each planned sidecar skip-not-clobber. A locked source or
    /// existing destination is caught and returned as a <see cref="MoveOutcome.LockedOrExists"/>
    /// skip (the primary move did NOT happen and no sidecars are touched); a permission failure as
    /// <see cref="MoveOutcome.PermissionDenied"/>. NEVER overwrites and NEVER touches a locking process.
    /// </summary>
    public MoveResult Move(string oldFull, string newFull, IReadOnlyList<SidecarMove>? sidecars = null)
    {
        try
        {
            EnsureParentDir(newFull);
            // 2-arg overload: throws IOException if newFull exists OR the source is locked.
            System.IO.File.Move(oldFull, newFull);
        }
        catch (IOException ex)
        {
            // Covers BOTH "destination exists" and "source locked/in-use". Skip + report; never force.
            return new MoveResult(false, MoveOutcome.LockedOrExists, [], [], $"locked or target exists: {ex.Message}");
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
                    // Skip-not-clobber: leave the pre-existing target untouched, warn.
                    warnings.Add($"sidecar target exists, skipped: {sc.To}");
                    continue;
                }

                try
                {
                    EnsureParentDir(sc.To);
                    System.IO.File.Move(sc.From, sc.To);
                    moved.Add(sc);
                }
                catch (IOException ex)
                {
                    // A locked/racy sidecar is non-fatal: warn and leave it (the primary already moved).
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

    /// <summary>
    /// Reverses a successful <see cref="Move"/> for the rollback path: moves the primary
    /// file <paramref name="newFull"/> → <paramref name="oldFull"/> and every moved sidecar back to
    /// its source. Best-effort: a secondary failure (e.g. the old slot got re-occupied) is swallowed
    /// and reported in the returned warnings rather than thrown, so a failed save's cleanup can never
    /// itself crash the batch. Returns the warnings (empty when the restore was clean).
    /// </summary>
    public IReadOnlyList<string> Rollback(string oldFull, string newFull, IReadOnlyList<SidecarMove> movedSidecars)
    {
        var warnings = new List<string>();

        // Reverse sidecars first (innermost moves undone first), then the primary file.
        for (int i = movedSidecars.Count - 1; i >= 0; i--)
        {
            var sc = movedSidecars[i];
            SafeMoveBack(sc.To, sc.From, warnings);
        }

        SafeMoveBack(newFull, oldFull, warnings);
        return warnings;
    }

    /// <summary>Best-effort move <paramref name="from"/> → <paramref name="to"/>; records (never throws) on failure.</summary>
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
            EnsureParentDir(to);
            System.IO.File.Move(from, to);
        }
        catch (Exception ex)
        {
            warnings.Add($"rollback move failed {from} -> {to}: {ex.Message}");
        }
    }

    private static void EnsureParentDir(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
