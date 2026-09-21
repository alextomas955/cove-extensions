using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync.Addressing;

/// <summary>The paths to ask the instance about for one Cove root, or why there are none.</summary>
/// <remarks>
/// A candidate is one root the instance declares followed by the sample file's tail below the Cove
/// root. Candidates is empty exactly when Refusal is non-null.
/// </remarks>
public sealed record FolderCandidates(
    IReadOnlyList<string> Candidates, FolderAgreementRefusal? Refusal);

/// <summary>What one Cove library root agreed with on the connected instance, or why it did not.</summary>
/// <remarks>
/// InstanceRoot is the instance's own spelling of the Cove root, and is non-null exactly when
/// Refusal is null. Tried lists the candidates in the order they were formed.
/// </remarks>
public sealed record FolderAgreementReading(
    string? InstanceRoot, FolderAgreementRefusal? Refusal, IReadOnlyList<string> Tried);

/// <summary>
/// What one Cove library root and the roots an instance declares make of each other.
/// </summary>
/// <remarks>
/// Pure, and performs no I/O. Whether a candidate really holds the sample file is a separate
/// reading, taken through the instance and folded back in by the caller.
/// <para>
/// The agreement is taken off the verified candidate by removing the tail that was appended to it,
/// not by searching the declared roots for a prefix. Instance roots that nest therefore need no
/// tie-break.
/// </para>
/// </remarks>
public static class FolderAgreement
{
    /// <summary>What to ask the instance about for <paramref name="coveRoot"/>.</summary>
    /// <remarks>
    /// A supplied mapping replaces the roots the instance declares rather than joining them, and
    /// the library's own spelling is not asked about either: an operator who states where a root is
    /// has settled it, and asking about the alternatives would restore the ambiguity the mapping
    /// removes. The mapping is still only a candidate, and the instance's answer to it decides.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="instanceRoots"/> is null.</exception>
    public static FolderCandidates CandidatesFor(
        string? sampleFilePath,
        string coveRoot,
        IReadOnlyList<string> instanceRoots,
        string? mapping)
    {
        ArgumentNullException.ThrowIfNull(instanceRoots);

        if (string.IsNullOrWhiteSpace(sampleFilePath))
        {
            return new FolderCandidates([], FolderAgreementRefusal.NoFileToProbeWith);
        }

        var supplied = !string.IsNullOrWhiteSpace(mapping);

        // A single Cove root to strip under, so exactly one tail is produced and each root to rebuild
        // under contributes one candidate.
        var reading = PathCandidateGuard.Read(
            sampleFilePath, [coveRoot], supplied ? [mapping!] : instanceRoots);
        if (reading.Refusal is { } refused)
        {
            return new FolderCandidates([], ReasonFor(refused));
        }

        if (supplied)
        {
            return new FolderCandidates(reading.Candidates, null);
        }

        // The library's own spelling is asked about too. Where both systems reach one filesystem at
        // one path, or the instance's root sits below the Cove root, no rebuilt candidate names the
        // file: the rebuild would carry segments the instance's own root already holds. It goes
        // last so a run reporting one of the paths it tried reports one the instance could hold.
        var asTheLibrarySpellsIt = PathCandidateGuard.Normalize(sampleFilePath);

        return new FolderCandidates(
            reading.Candidates.Contains(asTheLibrarySpellsIt, StringComparer.Ordinal)
                ? reading.Candidates
                : [.. reading.Candidates, asTheLibrarySpellsIt],
            null);
    }

    /// <summary>What the instance's answers make of the candidates.</summary>
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

    // A candidate was formed as a declared root followed by this tail, so what remains is that
    // root. Null where the candidate does not end in the tail, which the containment re-check can
    // produce by collapsing a parent segment.
    private static string? RootOf(string candidate, string tail)
    {
        var suffix = "/" + PathCandidateGuard.Normalize(tail).TrimStart('/');
        return candidate.EndsWith(suffix, StringComparison.Ordinal)
            ? candidate[..^suffix.Length]
            : null;
    }

    // The inbound refusal vocabulary is mapped here so it never reaches the outbound wire document.
    // Outbound the two root lists are swapped, so the reported-path reasons are all about the
    // sample file and the library-root reason is about the instance.
    private static FolderAgreementRefusal ReasonFor(PathCandidateRefusal refusal)
        => refusal switch
        {
            PathCandidateRefusal.NoLibraryRoots => FolderAgreementRefusal.InstanceDeclaresNoRoot,
            PathCandidateRefusal.EveryCandidateEscapedItsRoot
                => FolderAgreementRefusal.NothingResolved,
            _ => FolderAgreementRefusal.NoFileToProbeWith,
        };
}
