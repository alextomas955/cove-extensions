namespace Renamer.Execution;

// The same-volume decision, and the volume key every cross-volume grouping is keyed on. A same-volume
// result routes a move to the atomic File.Move path; a cross-volume result routes it to the verified
// copy path, and is also what arms the free-space guard and the preview's heavy-batch warning.
//
// The volume key is per-platform, because the two platforms express volume identity differently. On
// Windows it is the path root, compared case-insensitively. On Unix it is the mount point containing
// the path: Path.GetPathRoot returns "/" for every Unix path, so keying on it classifies two distinct
// mounts as one volume, and a move between mounts takes the atomic path with no free-space pre-check,
// no copy verification and no heavy-batch warning. File.Move still completes such a move, since .NET
// falls back to a copy, so what is lost is the checking and not the move.
//
// Passing mountPoints keeps a caller deterministic and off the disk; omitting it reads the real mount
// table once per process. A path under a mount that appears after that snapshot resolves to a shorter
// enclosing mount, which is a same-volume answer.
public static class VolumeClassifier
{
    private const string UnixRoot = "/";

    // The mount table changes rarely and a batch is short-lived, so one snapshot per process keeps a per-file
    // classification free of syscalls.
    private static readonly Lazy<IReadOnlyCollection<string>> RealMountPoints = new(ReadMountPoints);

    // The comparison is case-insensitive on Windows and case-sensitive elsewhere. It does not follow
    // PathOps.PathsEqual, which also ignores case on macOS: that comparer asks whether two paths name
    // one file, while this one compares mount-table volume keys, which are distinct entries even when
    // they differ only by case. Widening it would merge two real mounts and disable the cross-volume
    // copy, verify and delete path between them.
    public static bool SameVolume(string pathA, string pathB, IReadOnlyCollection<string>? mountPoints = null)
    {
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(VolumeKey(pathA, mountPoints), VolumeKey(pathB, mountPoints), cmp);
    }

    // The volume a path resides on: its path root on Windows, its enclosing mount point on Unix. Every
    // cross-volume grouping keys on this, so all of them agree with the same-volume split. A relative
    // or rootless path yields the empty string and is never resolved against the mount table.
    public static string VolumeKey(string path, IReadOnlyCollection<string>? mountPoints = null)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;

        if (OperatingSystem.IsWindows() || root.Length == 0)
        {
            return root;
        }

        string best = UnixRoot;
        foreach (string mount in mountPoints ?? RealMountPoints.Value)
        {
            string candidate = Normalize(mount);
            if (candidate.Length <= best.Length || !Encloses(candidate, path))
            {
                continue;
            }

            best = candidate;
        }

        return best;
    }

    // A mount point reaches us with or without its trailing separator depending on the source; compare one form.
    private static string Normalize(string mount)
        => mount.Length > 1 ? mount.TrimEnd('/') : mount;

    // Segment-boundary containment, so "/mnt/media2" is not read as living under "/mnt/media".
    private static bool Encloses(string mount, string path)
        => path.Equals(mount, StringComparison.Ordinal)
            || path.StartsWith(mount + UnixRoot, StringComparison.Ordinal);

    private static IReadOnlyCollection<string> ReadMountPoints()
    {
        try
        {
            return [.. DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable mount table degrades to root-only, so everything classifies as one volume.
            return [UnixRoot];
        }
    }
}
