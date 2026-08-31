namespace WhisparrSync.Import;

/// <summary>Why a reported path produced no candidate to probe.</summary>
public enum PathCandidateRefusal
{
    /// <summary>The delivery named no path.</summary>
    NoReportedPath,

    /// <summary>The reporting instance declares no root folder to take a tail below.</summary>
    NoReportedRoots,

    /// <summary>The reported path lies under none of the roots the instance declares.</summary>
    PathOutsideEveryReportedRoot,

    /// <summary>The host declares no library root to build a candidate under.</summary>
    NoLibraryRoots,

    /// <summary>Every candidate the tails produced left the root it was built under.</summary>
    EveryCandidateEscapedItsRoot,
}

/// <summary>What a reported path and the two systems' library roots make of each other.</summary>
/// <param name="Tails">
/// The parts of the reported path below each reporting root that contains it, in the order those
/// roots were given. Empty beside a non-null <paramref name="Refusal"/>.
/// </param>
/// <param name="Candidates">
/// The absolute paths to probe: every tail placed under every host library root, de-duplicated and
/// in the order they were formed. Empty beside a non-null <paramref name="Refusal"/>.
/// </param>
/// <param name="Refusal">Why there is nothing to probe, or null when there is.</param>
public sealed record PathCandidateReading(
    IReadOnlyList<string> Tails,
    IReadOnlyList<string> Candidates,
    PathCandidateRefusal? Refusal);

/// <summary>
/// Builds the absolute paths a reported file might really be at, under the host's own library roots.
/// </summary>
/// <remarks>
/// Pure, and performs no I/O. Whether a candidate is really that file is a separate reading, taken
/// through <see cref="IImportPathPort"/> and folded back in by the caller. Two rules here are
/// security-load-bearing rather than conveniences.
/// <para>
/// The path handed onward is always one this class CONSTRUCTED by joining a tail under a host
/// library root. The string the delivery reported is never passed through, and the host's own import
/// creates a folder row from whatever directory it is handed without consulting a library root at
/// all.
/// </para>
/// <para>
/// Each constructed candidate is canonicalized and then re-checked for containment under the root it
/// was built under. A parent-directory segment in the reported tail collapses during that step, so
/// the check has to come after it rather than before.
/// </para>
/// <para>
/// Where several reporting roots nest, every one that contains the reported path yields its own tail
/// and its own candidates. None is chosen here: which file is really meant is settled by what is on
/// disk, and a winner picked in arithmetic would be a guess the caller could not see.
/// </para>
/// </remarks>
public static class PathCandidateGuard
{
    /// <summary>The separator every path here is spelled with.</summary>
    /// <remarks>
    /// Deliberately not the platform's own. These paths are a Linux container's whichever machine
    /// this code runs on, so taking the separator from the running process would make the arithmetic
    /// answer differently in a test than in the container it describes.
    /// </remarks>
    private const string Separator = "/";

    /// <summary>What <paramref name="reportedPath"/> could resolve to on this host.</summary>
    /// <param name="reportedPath">The absolute path the delivery reported, in its own spelling.</param>
    /// <param name="reportedRoots">The library roots the reporting instance declares for itself.</param>
    /// <param name="libraryRoots">The host's own configured library paths.</param>
    /// <exception cref="ArgumentNullException">Either root list is null.</exception>
    public static PathCandidateReading Read(
        string? reportedPath,
        IReadOnlyList<string> reportedRoots,
        IReadOnlyList<string> libraryRoots)
    {
        ArgumentNullException.ThrowIfNull(reportedRoots);
        ArgumentNullException.ThrowIfNull(libraryRoots);

        if (string.IsNullOrWhiteSpace(reportedPath))
        {
            return Refused(PathCandidateRefusal.NoReportedPath);
        }

        var reporting = Usable(reportedRoots);
        if (reporting.Count == 0)
        {
            return Refused(PathCandidateRefusal.NoReportedRoots);
        }

        var tails = reporting
            .Select(root => TailBelow(reportedPath, root))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tails.Count == 0)
        {
            return Refused(PathCandidateRefusal.PathOutsideEveryReportedRoot);
        }

        var hosting = Usable(libraryRoots);
        if (hosting.Count == 0)
        {
            return new PathCandidateReading(tails, [], PathCandidateRefusal.NoLibraryRoots);
        }

        var candidates = hosting
            .SelectMany(root => tails.Select(tail => CandidateUnder(root, tail)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return candidates.Count == 0
            ? new PathCandidateReading(tails, [], PathCandidateRefusal.EveryCandidateEscapedItsRoot)
            : new PathCandidateReading(tails, candidates, null);
    }

    /// <summary>The one candidate that verified, or null when the number that did is not one.</summary>
    /// <remarks>
    /// Not-found and ambiguous are one answer here, and the caller acts on neither: both mean the
    /// product cannot say which file the delivery named, and it refuses rather than choosing.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="verified"/> is null.</exception>
    public static string? SingleVerified(IReadOnlyList<string> verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        return verified.Count == 1 ? verified[0] : null;
    }

    /// <summary>
    /// The part of <paramref name="path"/> below <paramref name="root"/>, or null when it is not
    /// under it.
    /// </summary>
    /// <remarks>
    /// Compared case-insensitively. Both strings come from the same instance describing its own
    /// filesystem, so a difference of case between them is a difference of spelling rather than of
    /// folder, and refusing on one would drop a file the instance can see.
    /// </remarks>
    internal static string? TailBelow(string path, string root)
    {
        var normalizedRoot = Normalize(root);
        if (normalizedRoot.Length == 0)
        {
            return null;
        }

        var prefix = normalizedRoot.EndsWith('/') ? normalizedRoot : normalizedRoot + "/";
        var normalizedPath = Normalize(path);
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedPath[prefix.Length..]
            : null;
    }

    /// <summary>
    /// <paramref name="tail"/> placed under <paramref name="root"/>, or null when the result leaves
    /// that root.
    /// </summary>
    /// <remarks>
    /// Compared ordinally. The two strings compared are the root and something built from it, so
    /// they differ in case only if a parent-directory segment moved the result elsewhere, which is
    /// the case this refuses.
    /// </remarks>
    internal static string? CandidateUnder(string root, string tail)
    {
        var normalizedRoot = Canonicalize(Normalize(root)).TrimEnd('/');
        if (normalizedRoot.Length == 0)
        {
            return null;
        }

        var candidate = Canonicalize(normalizedRoot + Separator + Normalize(tail).TrimStart('/'));
        return candidate.StartsWith(normalizedRoot + Separator, StringComparison.Ordinal)
            ? candidate
            : null;
    }

    /// <summary>One spelling of a path: forward slashes, no trailing separator beyond a bare root.</summary>
    internal static string Normalize(string path)
    {
        var slashed = path.Replace('\\', '/').Trim();
        return slashed.Length > 1 ? slashed.TrimEnd('/') : slashed;
    }

    /// <summary>
    /// <paramref name="path"/> with its current- and parent-directory segments collapsed.
    /// </summary>
    /// <remarks>
    /// Collapsed over the string rather than through the platform's own resolver, which anchors a
    /// path to the running process's drive and working directory. The paths here are a container's,
    /// and this code is also exercised on a machine whose separators and roots are not that
    /// container's.
    /// <para>
    /// A parent segment with nothing left to remove is dropped, so the result stays an absolute path
    /// and the containment check that follows sees a path shorter than its root rather than a
    /// spelling that could still start with one.
    /// </para>
    /// </remarks>
    internal static string Canonicalize(string path)
    {
        var rooted = path.StartsWith('/');
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        var joined = string.Join('/', segments);
        return rooted ? "/" + joined : joined;
    }

    private static List<string> Usable(IReadOnlyList<string> roots)
        => [.. roots.Where(root => !string.IsNullOrWhiteSpace(root))];

    private static PathCandidateReading Refused(PathCandidateRefusal refusal)
        => new([], [], refusal);
}
