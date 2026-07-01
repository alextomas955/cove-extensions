namespace Renamer.Execution;

/// <summary>
/// Pure-except-for-the-injected-probe cross-drive free-space decision. Sums the
/// projected file bytes PER DESTINATION VOLUME for a planned cross-drive batch and reports the
/// volumes whose need (summed bytes + a headroom margin) exceeds their available free space, so the
/// batch loop can refuse with a clear per-volume message rather than fill a disk.
/// <para>
/// The ONLY disk touch is the injected <see cref="System.Func{T,TResult}"/> free-space probe
/// (production: <c>vol =&gt; new System.IO.DriveInfo(vol).AvailableFreeSpace</c>); everything else is
/// <see cref="Path.GetPathRoot(string)"/> string math, so the guard is fully unit-testable with NO
/// real second drive. Like <see cref="VolumeClassifier"/> it touches no host types (no CoveContext).
/// </para>
/// <para>
/// Same-volume moves are EXCLUDED from every sum via <see cref="VolumeClassifier.SameVolume"/>
/// (REUSED — no hand-rolled root compare): an in-place/same-drive renamer consumes ~no extra space,
/// so a same-drive-only batch is never falsely refused.
/// </para>
/// <para>
/// <see cref="Shortfall"/> is a stateless decision over the projected moves and is safe to call
/// repeatedly — both for an up-front check and for an in-flight re-check (re-calling it mid-batch to
/// catch a concurrent scan shrinking the volume). The thread-safe parallel batch loop that consumes
/// <c>RenamerOptions.CrossVolumeConcurrency</c> + <see cref="PartitionByPair"/> lives in the batch
/// runner. Delete-as-you-go — the per-item copy→verify→delete primitive (<see cref="CrossVolumeMover"/>)
/// that frees each source as it goes, bounding peak extra space by the in-flight files — is
/// relied upon, not re-implemented here.
/// </para>
/// </summary>
public static class FreeSpaceGuard
{
    /// <summary>
    /// Returns the per-destination-volume shortfall for a planned cross-drive batch: for each
    /// destination volume whose summed projected bytes plus <paramref name="headroomBytes"/> exceed
    /// the volume's available free space, an entry of (Volume, Needed, Available). An EMPTY result
    /// means the whole batch fits.
    /// <list type="bullet">
    /// <item>Same-volume moves (<see cref="VolumeClassifier.SameVolume"/> is <c>true</c>) are
    /// excluded from every sum — they consume ~no extra space.</item>
    /// <item>Cross-volume moves are grouped by destination volume
    /// (<see cref="Path.GetPathRoot(string)"/> of the new path).</item>
    /// <item><c>Needed = sum(SizeBytes) + headroomBytes</c>; <c>Available =
    /// availableFreeSpace(volume)</c>; only volumes where <c>Needed &gt; Available</c> are returned.</item>
    /// </list>
    /// classify-not-throw: a malformed/rootless new path simply groups under its
    /// <see cref="Path.GetPathRoot(string)"/> value (possibly <c>""</c>); the method never throws on
    /// path content.
    /// </summary>
    /// <param name="moves">The projected moves — each a (current full path, new full path, file size in bytes) tuple. Decoupled from the planner's RenamerPlanItem so the guard needs no FileId→size lookup.</param>
    /// <param name="headroomBytes">The safety margin added to each volume's summed need before the comparison (<c>RenamerOptions.FreeSpaceHeadroomBytes</c>).</param>
    /// <param name="availableFreeSpace">The injected free-space probe (production: <c>vol =&gt; new DriveInfo(vol).AvailableFreeSpace</c>) — the ONLY disk touch.</param>
    public static IReadOnlyList<(string Volume, long Needed, long Available)> Shortfall(
        IEnumerable<(string OldFullPath, string NewFullPath, long SizeBytes)> moves,
        long headroomBytes,
        Func<string, long> availableFreeSpace)
    {
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(availableFreeSpace);

        var perVolume = moves
            .Where(m => !VolumeClassifier.SameVolume(m.OldFullPath, m.NewFullPath))   // cross-volume only
            .GroupBy(m => Path.GetPathRoot(m.NewFullPath) ?? string.Empty)
            .Select(g => (
                Volume: g.Key,
                Needed: g.Sum(m => m.SizeBytes) + headroomBytes,
                Available: availableFreeSpace(g.Key)));

        return [.. perVolume.Where(v => v.Needed > v.Available)];
    }

    /// <summary>
    /// Partitions the projected moves into groups the batch runner's bounded loop can throttle
    /// independently. Cross-volume moves are grouped by their
    /// (source-root, destination-root) disk pair so the runner can bound concurrency per pair
    /// (<c>RenamerOptions.CrossVolumeConcurrency</c>); same-volume moves are returned together under a
    /// single unthrottled group (<see cref="SameVolumePair"/>) because an atomic <c>File.Move</c>
    /// needs no throttle. Pure — only <see cref="Path.GetPathRoot(string)"/> string math, no I/O.
    /// This only exposes the grouping; the consuming parallel loop lives in the batch runner.
    /// </summary>
    public static IReadOnlyList<((string SourceRoot, string DestRoot) Pair, IReadOnlyList<(string OldFullPath, string NewFullPath, long SizeBytes)> Moves)> PartitionByPair(
        IEnumerable<(string OldFullPath, string NewFullPath, long SizeBytes)> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);

        return [.. moves
            .GroupBy(m => VolumeClassifier.SameVolume(m.OldFullPath, m.NewFullPath)
                ? SameVolumePair
                : (Path.GetPathRoot(m.OldFullPath) ?? string.Empty,
                   Path.GetPathRoot(m.NewFullPath) ?? string.Empty))
            .Select(g => (
                Pair: g.Key,
                Moves: (IReadOnlyList<(string OldFullPath, string NewFullPath, long SizeBytes)>)[.. g]))];
    }

    /// <summary>
    /// The sentinel (source,dest) pair under which all same-volume moves are grouped by
    /// <see cref="PartitionByPair"/>. Same-volume renames are unthrottled, so they share one group
    /// regardless of which drive they live on.
    /// </summary>
    public static (string SourceRoot, string DestRoot) SameVolumePair => (string.Empty, string.Empty);
}
