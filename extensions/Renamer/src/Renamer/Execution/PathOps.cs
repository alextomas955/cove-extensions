namespace Renamer.Execution;

/// <summary>
/// The pure path string math the planner and the execution slice share: slash normalization, the
/// directory/basename split, native-separator conversion, and the OS-aware path-equality rule.
/// </summary>
/// <remarks>
/// One home because <see cref="PathsEqual"/> and <see cref="VolumeClassifier"/> must agree on what counts as the
/// same path — a second copy diverging by case policy would let the disk-side self-exclusion and the volume
/// decision disagree about a single file. Touches no disk and no host type.
/// </remarks>
internal static class PathOps
{
    /// <summary>Every path is compared and split in forward-slash form, whatever separator it arrived with.</summary>
    internal static string NormalizeSlash(string p) => p.Replace('\\', '/');

    /// <summary>Converts back to the platform separator for a call that reaches the filesystem.</summary>
    internal static string ToNative(string p) => p.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>The directory portion of <paramref name="fullPath"/>, or the empty string when it has none.</summary>
    internal static string DirOf(string fullPath)
    {
        string p = NormalizeSlash(fullPath);
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p[..slash] : "";
    }

    /// <summary>The final segment of <paramref name="fullPath"/>, or the whole value when it has no separator.</summary>
    internal static string BasenameOf(string fullPath)
    {
        string p = NormalizeSlash(fullPath);
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p[(slash + 1)..] : p;
    }

    /// <summary>Whether two paths name the same location, ignoring case on Windows only.</summary>
    /// <remarks>A null <paramref name="a"/> compares as the empty string; this never throws.</remarks>
    internal static bool PathsEqual(string? a, string b)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(NormalizeSlash(a ?? ""), NormalizeSlash(b), cmp);
    }
}
