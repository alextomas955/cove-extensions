namespace Renamer.Execution;

/// <summary>
/// Pure, static same-vs-cross-volume decision derived solely from the path roots.
/// Performs NO I/O — no <see cref="System.IO"/> file access, no host types (no CoveContext) —
/// only <see cref="Path.GetPathRoot(string)"/> string math, so it is deterministic and cheap.
/// <para>
/// A <c>true</c> result (same volume) routes a move to the atomic <see cref="DiskMover"/>
/// <c>File.Move</c> fast path; a <c>false</c> result (different volume) routes it to
/// the verified cross-volume copy path (the <see cref="CrossVolumeMover"/>).
/// </para>
/// <para>
/// Shared by the executor (branch selection) and, later, the planner (preview target-volume
/// column) so both agree on what "same volume" means. The OS-aware case rule mirrors
/// <c>RenamerExecutor.PathsEqual</c> so volume identity and path identity agree on case.
/// </para>
/// </summary>
public static class VolumeClassifier
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="pathA"/> and <paramref name="pathB"/> share the
    /// same path root (same volume), <c>false</c> when their roots differ (cross volume).
    /// Comparison is <see cref="StringComparison.OrdinalIgnoreCase"/> on Windows and
    /// <see cref="StringComparison.Ordinal"/> elsewhere. Pure string math — touches no disk.
    /// </summary>
    public static bool SameVolume(string pathA, string pathB)
    {
        string? rootA = Path.GetPathRoot(pathA);
        string? rootB = Path.GetPathRoot(pathB);
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(rootA, rootB, cmp);
    }
}
