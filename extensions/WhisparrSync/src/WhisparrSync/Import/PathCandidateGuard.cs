using WhisparrSync.Contracts;

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
/// <remarks>
/// The reporting roots are those containing the reported path, in the order the instance gave them,
/// and the tails are the parts below each of them. The candidates are every tail placed under every
/// host library root, de-duplicated and in the order they were formed. Tails and candidates are
/// empty beside a non-null refusal.
/// </remarks>
public sealed record PathCandidateReading(
    IReadOnlyList<string> ReportingRoots,
    IReadOnlyList<string> Tails,
    IReadOnlyList<string> Candidates,
    PathCandidateRefusal? Refusal)
{
    /// <summary>The reporting root a refusal from this reading is counted under.</summary>
    /// <remarks>
    /// Blank when no reporting root contains the reported path. Where several reporting roots nest,
    /// the first the instance listed carries the line. This groups a count and never decides which
    /// file to import.
    /// </remarks>
    public string RefusalRoot => ReportingRoots.Count > 0 ? ReportingRoots[0] : "";
}

/// <summary>
/// One candidate path, as <see cref="PathCandidateGuard"/> constructed it, and what a probe found
/// there.
/// </summary>
public sealed record ProbedCandidate(string Path, ProbedPath Probed);

/// <summary>The one path to import, or why there is none.</summary>
/// <remarks>
/// Exactly one of the two members is set, and nothing outside <see cref="PathCandidateGuard"/> can
/// construct a reading in which that is false.
/// </remarks>
public sealed record PathResolution
{
    private PathResolution(string? path, ImportRefusalCause? cause)
    {
        Path = path;
        Cause = cause;
    }

    /// <summary>The path to import, or null when <see cref="Cause"/> says why there is none.</summary>
    public string? Path { get; }

    /// <summary>Why nothing is to be imported, or null when <see cref="Path"/> names what is.</summary>
    public ImportRefusalCause? Cause { get; }

    internal static PathResolution Import(string path) => new(path, null);

    internal static PathResolution Refuse(ImportRefusalCause cause) => new(null, cause);
}

/// <summary>
/// Builds the absolute paths one file might really be at, under another system's own library roots.
/// </summary>
/// <remarks>
/// The arithmetic is direction-neutral. Inbound it is called with the reporting instance's roots to
/// strip under and the host's library roots to rebuild under; outbound those two are swapped, with
/// a single library root to strip under so exactly one tail is produced.
/// <para>
/// Pure, and performs no I/O. Whether a candidate is really that file is a separate reading, taken
/// through <see cref="IImportPathPort"/> inbound and through the instance outbound.
/// </para>
/// <para>
/// The path handed onward is always one this class constructed by joining a tail under a host
/// library root. The string the delivery reported is never passed through, because the host's own
/// import creates a folder row from whatever directory it is handed without consulting a library
/// root.
/// </para>
/// <para>
/// Each constructed candidate is canonicalized and then re-checked for containment under the root
/// it was built under. A parent-directory segment in the reported tail collapses during that step,
/// so the check has to come after it.
/// </para>
/// <para>
/// Where several reporting roots nest, every one containing the reported path yields its own tail
/// and its own candidates, and none is chosen here: what is on disk settles which file is meant.
/// </para>
/// <para>
/// Containment under a host library root is a yes-or-no gate and never selects one. The host's
/// import takes an absolute path and no root.
/// </para>
/// </remarks>
public static class PathCandidateGuard
{
    // Not the platform's separator. These paths are a Linux container's whichever machine this code
    // runs on, so taking it from the running process would answer differently in a test.
    private const string Separator = "/";

    /// <summary>
    /// What <paramref name="path"/> could resolve to: a tail taken below each of
    /// <paramref name="strippedUnder"/> and placed under each of <paramref name="rebuiltUnder"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either root list is null.</exception>
    public static PathCandidateReading Read(
        string? path,
        IReadOnlyList<string> strippedUnder,
        IReadOnlyList<string> rebuiltUnder)
    {
        ArgumentNullException.ThrowIfNull(strippedUnder);
        ArgumentNullException.ThrowIfNull(rebuiltUnder);

        if (string.IsNullOrWhiteSpace(path))
        {
            return Refused(PathCandidateRefusal.NoReportedPath);
        }

        var reporting = Usable(strippedUnder);
        if (reporting.Count == 0)
        {
            return Refused(PathCandidateRefusal.NoReportedRoots);
        }

        var containing = reporting
            .Where(root => TailBelow(path, root) is not null)
            .ToList();
        var tails = containing
            .Select(root => TailBelow(path, root))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tails.Count == 0)
        {
            return Refused(PathCandidateRefusal.PathOutsideEveryReportedRoot);
        }

        var hosting = Usable(rebuiltUnder);
        if (hosting.Count == 0)
        {
            return new PathCandidateReading(containing, tails, [], PathCandidateRefusal.NoLibraryRoots);
        }

        var candidates = hosting
            .SelectMany(root => tails.Select(tail => CandidateUnder(root, tail)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return candidates.Count == 0
            ? new PathCandidateReading(
                containing, tails, [], PathCandidateRefusal.EveryCandidateEscapedItsRoot)
            : new PathCandidateReading(containing, tails, candidates, null);
    }

    /// <summary>What the probe results make of the candidates.</summary>
    /// <remarks>
    /// One verified candidate is the file the delivery named; none and more than one are separate
    /// refusals, because a misconfigured root and one absent file are different things to act on.
    /// A null <paramref name="reportedSize"/> verifies on presence alone and is not a mismatch.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="probed"/> is null.</exception>
    public static PathResolution Resolve(IReadOnlyList<ProbedCandidate> probed, long? reportedSize)
    {
        ArgumentNullException.ThrowIfNull(probed);

        var verified = probed
            .Where(candidate => Verifies(candidate, reportedSize))
            .Select(candidate => candidate.Path)
            .ToList();

        return verified.Count switch
        {
            1 => PathResolution.Import(verified[0]),
            0 => PathResolution.Refuse(ImportRefusalCause.NotFoundUnderAnyRoot),
            _ => PathResolution.Refuse(ImportRefusalCause.AmbiguousCandidates),
        };
    }

    // A file of the right name and a different length is a different file. Both generations report
    // a size, so a reported one is always checked.
    internal static bool Verifies(ProbedCandidate candidate, long? reportedSize)
        => candidate.Probed.Exists
            && (reportedSize is null || candidate.Probed.Size == reportedSize);

    // The part of the path below the root, or null when it is not under it. The prefix carries a
    // trailing separator, so a sibling whose name starts with the root's is not matched.
    // Compared case-insensitively: both strings come from the same instance describing its own
    // filesystem, so a difference of case is a difference of spelling and not of folder.
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

    // The tail placed under the root, or null when the result leaves that root. Containment is
    // checked against the root plus a separator, ordinally: the candidate is built from the root,
    // so a difference of case means a parent-directory segment moved the result elsewhere.
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

    // One spelling of a path: forward slashes, no trailing separator beyond a bare root.
    internal static string Normalize(string path)
    {
        var slashed = path.Replace('\\', '/').Trim();
        return slashed.Length > 1 ? slashed.TrimEnd('/') : slashed;
    }

    // Collapses current- and parent-directory segments over the string, not through the platform's
    // resolver, which anchors a path to the running process's drive and working directory.
    // A parent segment with nothing left to remove is dropped, so the result stays absolute and the
    // containment check that follows sees a path shorter than its root.
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
        => new([], [], [], refusal);
}
