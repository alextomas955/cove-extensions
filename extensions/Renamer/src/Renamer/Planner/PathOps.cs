namespace Renamer.Planner;

// The pure path string math the engine, the planner and the execution slice share.
//
// Every site deciding whether two paths name the same file applies the one case policy stated here. A
// second copy diverging from it would let the disk-side self-exclusion and the planner's confinement
// disagree about a single file. VolumeClassifier is not one of those sites: it compares volume keys,
// not filenames.
internal static class PathOps
{
    // Paths are compared and split in forward-slash form, whatever separator they arrived with.
    internal static string NormalizeSlash(string p) => p.Replace('\\', '/');

    // Converts back to the platform separator for a call that reaches the filesystem.
    internal static string ToNative(string p) => p.Replace('/', Path.DirectorySeparatorChar);

    // Empty when the path has no directory portion.
    internal static string DirOf(string fullPath)
    {
        string p = NormalizeSlash(fullPath);
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p[..slash] : "";
    }

    // The whole value when it has no separator.
    internal static string BasenameOf(string fullPath)
    {
        string p = NormalizeSlash(fullPath);
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p[(slash + 1)..] : p;
    }

    // Splits at the final dot, which is kept on the extension. A leading dot is not a split point, so a
    // dotfile keeps its whole name and gets no extension.
    internal static (string filename, string ext) SplitBasename(string basename)
    {
        int dot = basename.LastIndexOf('.');
        return dot > 0 ? (basename[..dot], basename[dot..]) : (basename, "");
    }

    // The name without its final extension, so "video.en.vtt" gives "video.en".
    internal static string StemOf(string basename)
    {
        int dot = basename.LastIndexOf('.');
        return dot > 0 ? basename[..dot] : basename;
    }

    // Joins a folder part and a name part into one forward-slash path, tolerating an empty part on
    // either side. Normalization happens here, so the result is canonical whichever separator either
    // part arrived with.
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

    // Inserts the suffix counter before the extension, so "name", " ({n})" and ".mkv" give "name (1).mkv".
    internal static string ApplySuffix(string filename, string ext, string suffixFormat, int counter)
        => filename
            + suffixFormat.Replace("{n}", counter.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + ext;

    // Whether two paths name the same location, ignoring case on the platforms whose default
    // filesystem is case-insensitive and comparing ordinally elsewhere. A null first argument compares
    // as the empty string, and this never throws.
    //
    // Where the volume is case-insensitive a path and its case-variant are one physical file, so a
    // case-only rename finds File.Exists(target) true. Unless self-path equality ignores case, the
    // executor reads its own source as an occupant and adds a needless suffix.
    //
    // The residual gap: the OS is an approximation of filesystem semantics. APFS is case-insensitive by
    // default but can be formatted case-sensitive, where this comparer is over-permissive, and a Linux
    // host mounting CIFS or SMB is case-insensitive while this comparer stays ordinal, where the
    // needless suffix survives. Closing it needs a per-volume probe. The cross-file no-clobber
    // guarantee does not depend on this rule.
    internal static bool PathsEqual(string? a, string b) =>
        PathComparer.Equals(NormalizeSlash(a ?? ""), NormalizeSlash(b));

    // The PathsEqual case rule as a comparer, for keying normalized paths. Its input must already be in
    // forward-slash form.
    internal static StringComparer PathComparer =>
        PathsIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Whether a path and its case-variant name one physical file on this platform. The rule
    // PathComparer selects on, as a value, for a caller that must express the comparison where a
    // StringComparer does not reach, such as a database query whose collation need not agree with the
    // volume.
    internal static bool PathsIgnoreCase => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    // A cross-volume copy is written beside its destination under this marker plus InFlightRandomChars
    // hex characters, then promoted. The planner measures a destination against that longer name.
    internal const string InFlightMarker = ".rnm";

    internal const int InFlightRandomChars = 8;

    internal static readonly int InFlightSuffixLength = InFlightMarker.Length + InFlightRandomChars;
}
