namespace Renamer.Execution;

/// <summary>
/// Same-vs-cross-volume decision, and the volume key every cross-volume grouping is keyed on.
/// <para>
/// A <c>true</c> <see cref="SameVolume"/> result routes a move to the atomic <see cref="DiskMover"/>
/// <c>File.Move</c> fast path; <c>false</c> routes it to the verified cross-volume copy path (the
/// <see cref="CrossVolumeMover"/>), which is also what arms the free-space guard and the preview's heavy-batch
/// warning.
/// </para>
/// </summary>
/// <remarks>
/// The volume key is per-platform because the two express volume identity differently:
/// <list type="bullet">
/// <item>Windows — the path root (<c>C:\</c>), compared case-insensitively.</item>
/// <item>Unix — the MOUNT POINT containing the path. <see cref="Path.GetPathRoot(string)"/> returns <c>/</c> for
/// every Unix path, so keying on it alone classified two distinct mounts as one volume: on Linux (Cove's usual
/// host) a move between mounts silently took the atomic path, skipping the free-space pre-check, the copy
/// verification, and the heavy-batch warning. <c>File.Move</c> still completes such a move — .NET falls back to
/// copy internally — so what was lost was the safety spine, not the move.</item>
/// </list>
/// Pass <c>mountPoints</c> to keep a caller deterministic and disk-free; omit it and the real mount table is read
/// once per process. A path under a mount that appears after that snapshot resolves to a shorter enclosing mount,
/// which is the pre-existing same-volume answer — never a worse one.
/// </remarks>
public static class VolumeClassifier
{
    private const string UnixRoot = "/";

    // The mount table changes rarely and a batch is short-lived, so one snapshot per process keeps a per-file
    // classification free of syscalls.
    private static readonly Lazy<IReadOnlyCollection<string>> RealMountPoints = new(ReadMountPoints);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="pathA"/> and <paramref name="pathB"/> live on the same volume,
    /// <c>false</c> when they are cross-volume.
    /// </summary>
    /// <remarks>Comparison is case-insensitive on Windows and case-sensitive elsewhere, mirroring
    /// <c>RenamerExecutor.PathsEqual</c> so volume identity and path identity agree on case.</remarks>
    public static bool SameVolume(string pathA, string pathB, IReadOnlyCollection<string>? mountPoints = null)
    {
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(VolumeKey(pathA, mountPoints), VolumeKey(pathB, mountPoints), cmp);
    }

    /// <summary>
    /// The volume <paramref name="path"/> resides on: its path root on Windows, its enclosing mount point on Unix.
    /// </summary>
    /// <remarks>
    /// Every cross-volume grouping (the free-space guard's per-destination sums, the batch runner's
    /// source/destination pair partition, the preview's volume-pair deltas) keys on this, so all of them agree with
    /// the same/cross split. A relative or rootless path yields the empty string and is never resolved against the
    /// mount table.
    /// </remarks>
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
        catch (Exception)
        {
            // An unreadable mount table degrades to root-only, which is the pre-existing classification.
            return [UnixRoot];
        }
    }
}
