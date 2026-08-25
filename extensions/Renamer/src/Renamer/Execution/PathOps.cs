namespace Renamer.Execution;

/// <summary>
/// The pure path string math the planner and the execution slice share: slash normalization, the
/// directory/basename split, the name/extension split, the suffix and join rules, native-separator
/// conversion, and the OS-aware path-equality rule.
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

    /// <summary>Splits a basename at its final dot into the name and the extension (dot included).</summary>
    /// <remarks>A leading dot is not a split point, so a dotfile keeps its whole name and no extension.</remarks>
    internal static (string filename, string ext) SplitBasename(string basename)
    {
        int dot = basename.LastIndexOf('.');
        return dot > 0 ? (basename[..dot], basename[dot..]) : (basename, "");
    }

    /// <summary>The stem (name without its final extension): "video.mkv" → "video"; "video.en.vtt" → "video.en".</summary>
    internal static string StemOf(string basename)
    {
        int dot = basename.LastIndexOf('.');
        return dot > 0 ? basename[..dot] : basename;
    }

    /// <summary>
    /// Joins a folder part and a name part into one forward-slash path, tolerating an empty part on
    /// either side.
    /// </summary>
    /// <remarks>
    /// Normalization happens HERE rather than at a call site, so the result is canonical whichever
    /// separator either part arrived with and the operation cannot be called wrongly.
    /// </remarks>
    internal static string JoinPath(string a, string b)
    {
        string left = NormalizeSlash(a);
        string right = NormalizeSlash(b);

        if (string.IsNullOrEmpty(left))
        {
            return right;
        }

        if (string.IsNullOrEmpty(right))
        {
            return left;
        }

        return left.TrimEnd('/') + "/" + right.TrimStart('/');
    }

    /// <summary>Inserts the suffix counter before the extension (e.g. "name" + " ({n})" + ".mkv" → "name (1).mkv").</summary>
    internal static string ApplySuffix(string filename, string ext, string suffixFormat, int counter)
        => filename
            + suffixFormat.Replace("{n}", counter.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + ext;

    /// <summary>Whether two paths name the same location, ignoring case on Windows only.</summary>
    /// <remarks>A null <paramref name="a"/> compares as the empty string; this never throws.</remarks>
    internal static bool PathsEqual(string? a, string b)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(NormalizeSlash(a ?? ""), NormalizeSlash(b), cmp);
    }
}
