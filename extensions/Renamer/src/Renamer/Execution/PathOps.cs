namespace Renamer.Execution;

/// <summary>
/// The pure path string math the planner and the execution slice share: slash normalization, the
/// directory/basename split, native-separator conversion, and the OS-aware path-equality rule.
/// </summary>
/// <remarks>
/// One home because every site deciding whether two paths name the same FILE must apply one case
/// policy — a second copy diverging from it would let the disk-side self-exclusion and the planner's
/// confinement disagree about a single file. <see cref="VolumeClassifier"/> is deliberately NOT one of
/// those sites: it compares volume keys, not filenames (see its own remarks). Touches no disk and no
/// host type.
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

    /// <summary>
    /// Joins a folder part and a name part into one forward-slash path, tolerating an empty part on
    /// either side.
    /// </summary>
    /// <remarks>
    /// Normalization happens HERE rather than at a call site, so the result is canonical whichever
    /// separator either part arrived with and the operation cannot be called wrongly. The three
    /// private copies this replaced each relied on a different part of that contract — one normalized
    /// its folder argument, one trimmed only the forward slash, one guarded neither empty part — and
    /// every former caller now gets the superset of all three.
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

    /// <summary>
    /// Whether two paths name the same location, ignoring case on the platforms whose default
    /// filesystem is case-insensitive (Windows and macOS) and comparing ordinally elsewhere.
    /// </summary>
    /// <remarks>
    /// This is the CANONICAL statement of the case rule; the other four comparer sites
    /// (<c>EmptySourceFolderCleaner</c> ×2, <c>DestinationResolver.SourcePathComparer</c>,
    /// <c>PathConfinement.IsUnderRoot</c>) point here rather than restate it.
    /// <para>
    /// Why case must be ignored where the volume is case-insensitive: there, a path and its
    /// case-variant are ONE physical file. So a case-only rename (<c>movie.mkv</c> →
    /// <c>Movie.mkv</c>) finds <c>File.Exists(target)</c> true, and unless self-path equality
    /// ignores case the executor reads its own source as an occupant and suffixes a needless
    /// <c>Movie (1).mkv</c>. Keying on <c>IsWindows()</c> alone did exactly that on macOS.
    /// </para>
    /// <para>
    /// The known residual gap, stated rather than hidden: the OS is an APPROXIMATION of filesystem
    /// semantics, not a reading of them. APFS is case-insensitive by default but CAN be formatted
    /// case-sensitive (this comparer is then over-permissive there), and a Linux host mounting
    /// CIFS/SMB — common for a media library — is case-insensitive while this comparer stays
    /// Ordinal (under-permissive: the suffix bug survives on that mount). Closing the gap needs a
    /// per-volume probe rather than an OS test; the cross-file no-clobber guarantee does not
    /// depend on it, and is pinned independently by
    /// <c>CaseOnlyRenamerTests.DifferentFileAtCaseVariantName_StillCollides_NoClobber</c>.
    /// </para>
    /// A null <paramref name="a"/> compares as the empty string; this never throws.
    /// </remarks>
    internal static bool PathsEqual(string? a, string b)
    {
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(NormalizeSlash(a ?? ""), NormalizeSlash(b), cmp);
    }
}
