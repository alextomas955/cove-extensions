using Renamer.Planner;

namespace Renamer.Execution;

// The cross-drive free-space decision. It sums the projected file bytes per destination volume and
// reports the volumes whose need, the summed bytes plus a headroom margin, exceeds their available
// free space, so the batch loop can refuse per volume instead of filling a disk.
//
// The only disk touch is the injected free-space probe; everything else is VolumeClassifier string
// math, so the guard is testable with no second drive.
//
// Same-volume moves are excluded from every sum, because an in-place rename consumes no extra space
// and a same-drive-only batch must never be refused.
//
// Shortfall is stateless over the projected moves and safe to call repeatedly, both up front and
// mid-batch to catch a concurrent scan shrinking the volume. CrossVolumeMover frees each source as it
// goes, which bounds peak extra space by the files in flight.
public static class FreeSpaceGuard
{
    // Returns (Volume, Needed, Available) for each destination volume whose summed projected bytes plus
    // headroomBytes exceed its available free space. An empty result means the whole batch fits.
    //
    // A malformed or rootless new path groups under its VolumeClassifier key, which may be empty; the
    // method never throws on path content.
    //
    // The moves are decoupled from the planner's plan item so the guard needs no file-id-to-size lookup.
    public static IReadOnlyList<(string Volume, long Needed, long Available)> Shortfall(
        IEnumerable<(string OldFullPath, string NewFullPath, long SizeBytes)> moves,
        long headroomBytes,
        Func<string, long> availableFreeSpace,
        IReadOnlyCollection<string>? mountPoints = null)
    {
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(availableFreeSpace);

        var perVolume = moves
            .Where(m => !VolumeClassifier.SameVolume(m.OldFullPath, m.NewFullPath, mountPoints))
            .GroupBy(m => VolumeClassifier.VolumeKey(m.NewFullPath, mountPoints))
            .Select(g => (
                Volume: g.Key,
                Needed: g.Sum(m => m.SizeBytes) + headroomBytes,
                Available: availableFreeSpace(g.Key)));

        return [.. perVolume.Where(v => v.Needed > v.Available)];
    }

    // Partitions the projected moves into groups the batch runner's bounded loop throttles
    // independently. Cross-volume moves group by their (source root, destination root) disk pair;
    // same-volume moves share one group under SameVolumePair.
    //
    // The runner processes the returned groups in sequence, so a batch's peak concurrency is one
    // group's bound and never the sum over the groups.
    public static IReadOnlyList<((string SourceRoot, string DestRoot) Pair, IReadOnlyList<T> Items)> PartitionByPair<T>(
        IEnumerable<T> items,
        Func<T, (string OldFullPath, string NewFullPath)> move,
        IReadOnlyCollection<string>? mountPoints = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(move);

        return [.. items
            .GroupBy(item =>
            {
                var (oldFullPath, newFullPath) = move(item);
                return VolumeClassifier.SameVolume(oldFullPath, newFullPath, mountPoints)
                    ? SameVolumePair
                    : (VolumeClassifier.VolumeKey(oldFullPath, mountPoints),
                       VolumeClassifier.VolumeKey(newFullPath, mountPoints));
            })
            .Select(g => (Pair: g.Key, Items: (IReadOnlyList<T>)[.. g]))];
    }

    // The sentinel pair every same-volume move groups under. Same-volume renames are bounded by one
    // concurrency setting, so they share one group whichever drive they live on.
    public static (string SourceRoot, string DestRoot) SameVolumePair => (string.Empty, string.Empty);
}
