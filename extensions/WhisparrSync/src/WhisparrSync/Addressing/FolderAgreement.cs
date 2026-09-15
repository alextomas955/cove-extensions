using WhisparrSync.Import;

namespace WhisparrSync.Addressing;

/// <summary>Why one Cove library root has no agreed spelling on the connected instance.</summary>
public enum FolderAgreementRefusal
{
    /// <summary>The instance declares no root to rebuild a tail under.</summary>
    InstanceDeclaresNoRoot,

    /// <summary>The library holds no file under this root to establish an agreement from.</summary>
    NoFileToProbeWith,

    /// <summary>No candidate held a file of the size the library holds.</summary>
    NothingResolved,

    /// <summary>Several candidates held one, so which of them the root means is not established.</summary>
    MoreThanOneResolved,

    /// <summary>
    /// The instance was asked and its answer could not be read, so nothing was established either
    /// way. Distinct from <see cref="NothingResolved"/>, which is an answer.
    /// </summary>
    ProbeCouldNotBeRead,

    /// <summary>The connected generation holds no role to ask the instance through.</summary>
    InstanceCannotBeAsked,

    /// <summary>The folder sits under none of the host's configured library roots.</summary>
    FolderUnderNoLibraryRoot,
}

/// <summary>The paths to ask the instance about for one Cove root, or why there are none.</summary>
/// <param name="Candidates">
/// One path per root the instance declares: that root followed by the sample file's tail below the
/// Cove root. Empty beside a non-null <paramref name="Refusal"/>.
/// </param>
/// <param name="Refusal">Why there is nothing to ask about, or null when there is.</param>
public sealed record FolderCandidates(
    IReadOnlyList<string> Candidates, FolderAgreementRefusal? Refusal);

/// <summary>What one Cove library root agreed with on the connected instance, or why it did not.</summary>
/// <param name="InstanceRoot">
/// The instance's own spelling of the Cove root, or null beside a non-null
/// <paramref name="Refusal"/>.
/// </param>
/// <param name="Refusal">Why nothing was agreed, or null when something was.</param>
/// <param name="Tried">Every candidate the instance was asked about, in the order they were formed.</param>
public sealed record FolderAgreementReading(
    string? InstanceRoot, FolderAgreementRefusal? Refusal, IReadOnlyList<string> Tried);

/// <summary>
/// What one Cove library root and the roots an instance declares make of each other.
/// </summary>
/// <remarks>
/// Pure, and performs no I/O. Whether a candidate really holds the sample file is a separate reading,
/// taken through the instance and folded back in by the caller.
/// <para>
/// The candidates are built by <see cref="PathCandidateGuard"/>, which is the same arithmetic the
/// inbound direction uses with its two root lists the other way round. One statement of the
/// stripping, the rebuilding and the containment re-check serves both directions.
/// </para>
/// <para>
/// The agreement is taken off the verified candidate by removing the tail that was appended to it,
/// rather than by searching the declared roots for one that is a prefix. Instance roots that nest
/// therefore need no tie-break.
/// </para>
/// </remarks>
public static class FolderAgreement
{
    /// <summary>What to ask the instance about for <paramref name="coveRoot"/>.</summary>
    /// <param name="sampleFilePath">One file the library holds under <paramref name="coveRoot"/>.</param>
    /// <param name="coveRoot">The Cove library root the sample file sits under.</param>
    /// <param name="instanceRoots">The roots the connected instance declares for itself.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instanceRoots"/> is null.</exception>
    public static FolderCandidates CandidatesFor(
        string? sampleFilePath, string coveRoot, IReadOnlyList<string> instanceRoots)
    {
        ArgumentNullException.ThrowIfNull(instanceRoots);

        if (string.IsNullOrWhiteSpace(sampleFilePath))
        {
            return new FolderCandidates([], FolderAgreementRefusal.NoFileToProbeWith);
        }

        // A single Cove root to strip under, so exactly one tail is produced and each declared root
        // contributes one candidate.
        var reading = PathCandidateGuard.Read(sampleFilePath, [coveRoot], instanceRoots);
        if (reading.Refusal is { } refused)
        {
            return new FolderCandidates([], ReasonFor(refused));
        }

        // The library's own spelling is asked about too. Where both systems reach one filesystem at
        // one path, and where the instance's root sits below the Cove root, no rebuilt candidate
        // names the file: the rebuild would carry the segments the instance's own root already holds.
        // Only the instance's answer tells that deployment from one whose mounts differ. Last, so a
        // run reporting one of the paths it tried reports one the instance could have held.
        var asTheLibrarySpellsIt = PathCandidateGuard.Normalize(sampleFilePath);

        return new FolderCandidates(
            reading.Candidates.Contains(asTheLibrarySpellsIt, StringComparer.Ordinal)
                ? reading.Candidates
                : [.. reading.Candidates, asTheLibrarySpellsIt],
            null);
    }

    /// <summary>What the instance's answers make of the candidates.</summary>
    /// <param name="sampleFilePath">The file the candidates were built from.</param>
    /// <param name="coveRoot">The Cove library root that file sits under.</param>
    /// <param name="probed">Each candidate and what the instance reported at it.</param>
    /// <param name="sampleSize">The size the library holds for the sample file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="probed"/> is null.</exception>
    public static FolderAgreementReading Resolve(
        string sampleFilePath,
        string coveRoot,
        IReadOnlyList<ProbedCandidate> probed,
        long sampleSize)
    {
        ArgumentNullException.ThrowIfNull(probed);

        var tried = probed.Select(candidate => candidate.Path).ToList();
        var verified = probed
            .Where(candidate => PathCandidateGuard.Verifies(candidate, sampleSize))
            .Select(candidate => candidate.Path)
            .ToList();

        if (verified.Count > 1)
        {
            return new FolderAgreementReading(
                null, FolderAgreementRefusal.MoreThanOneResolved, tried);
        }

        var tail = PathCandidateGuard.TailBelow(sampleFilePath, coveRoot);
        var agreed = verified.Count == 1 && tail is not null
            ? RootOf(verified[0], tail)
            : null;

        return agreed is null
            ? new FolderAgreementReading(null, FolderAgreementRefusal.NothingResolved, tried)
            : new FolderAgreementReading(agreed, null, tried);
    }

    /// <summary>
    /// <paramref name="folder"/> as <paramref name="instanceRoot"/> spells it, or null when it is not
    /// under <paramref name="coveRoot"/>.
    /// </summary>
    public static string? Address(string folder, string coveRoot, string instanceRoot)
    {
        var tail = PathCandidateGuard.TailBelow(folder, coveRoot);
        return tail is null ? null : PathCandidateGuard.CandidateUnder(instanceRoot, tail);
    }

    /// <summary>
    /// <paramref name="candidate"/> with <paramref name="tail"/> removed from its end.
    /// </summary>
    /// <remarks>
    /// A candidate was formed as a declared root followed by this tail, so what remains is that root.
    /// Null where the candidate's spelling does not end in the tail, which the containment re-check
    /// inside the arithmetic can produce by collapsing a parent segment.
    /// </remarks>
    private static string? RootOf(string candidate, string tail)
    {
        var suffix = "/" + PathCandidateGuard.Normalize(tail).TrimStart('/');
        return candidate.EndsWith(suffix, StringComparison.Ordinal)
            ? candidate[..^suffix.Length]
            : null;
    }

    // The inbound vocabulary never leaves this module. Outbound surfaces none of it, and mapping it
    // here keeps the inbound refusal projection and the wire document it feeds unchanged.
    //
    // Outbound the two root lists are swapped, so the reported-path reasons are all about the sample
    // file and the library-root reason is about the instance.
    private static FolderAgreementRefusal ReasonFor(PathCandidateRefusal refusal)
        => refusal switch
        {
            PathCandidateRefusal.NoLibraryRoots => FolderAgreementRefusal.InstanceDeclaresNoRoot,
            PathCandidateRefusal.EveryCandidateEscapedItsRoot
                => FolderAgreementRefusal.NothingResolved,
            _ => FolderAgreementRefusal.NoFileToProbeWith,
        };
}
