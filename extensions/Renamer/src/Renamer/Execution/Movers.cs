namespace Renamer.Execution;

// One planned sidecar move, absolute on both sides. Either separator is accepted.
public readonly record struct SidecarMove(string From, string To);

// The outcome of a move by either tier. MovedSidecars holds the pairs that actually moved, in move
// order, which is what a rollback reverses. Reason is null on success. A move that did not happen is a
// skip and never a thrown error.
public sealed record MoveResult(
    bool Moved,
    MoveOutcome Outcome,
    IReadOnlyList<SidecarMove> MovedSidecars,
    IReadOnlyList<string> Warnings,
    string? Reason);

// Picks the tier for a move. A same-volume move is DiskMover's atomic rename; a cross-volume move is
// CrossVolumeMover's copy, verify, promote and delete-source-last. A rollback goes back through the
// tier that moved the file.
internal static class Movers
{
    public static async Task<MoveResult> MoveAsync(
        bool sameVolume, CrossVolumeMover cross, string from, string to,
        IReadOnlyList<SidecarMove> sidecars, CancellationToken ct)
        => sameVolume
            ? DiskMover.Move(from, to, sidecars)
            : await cross.MoveAsync(from, to, sidecars, ct);

    public static async Task<IReadOnlyList<string>> RollbackAsync(
        bool sameVolume, CrossVolumeMover cross, string oldFull, string newFull,
        IReadOnlyList<SidecarMove> movedSidecars, CancellationToken ct)
        => sameVolume
            ? DiskMover.Rollback(oldFull, newFull, movedSidecars)
            : await cross.RollbackAsync(oldFull, newFull, movedSidecars, ct);

    public static void EnsureParentDir(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
