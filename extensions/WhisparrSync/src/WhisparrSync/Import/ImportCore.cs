using Microsoft.Extensions.Logging;

namespace WhisparrSync.Import;

/// <inheritdoc cref="IImportCore"/>
internal sealed class ImportCore(
    IReportedRootPort reportedRoots,
    ICoveLibraryPort library,
    IImportPathPort paths,
    ILogger log) : IImportCore
{
    public async Task<ImportOutcome> IngestAsync(ImportCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var reading = PathCandidateGuard.Read(
            candidate.ReportedPath,
            await reportedRoots.ReadAsync(candidate.Generation, ct).ConfigureAwait(false),
            library.LibraryRoots);

        if (reading.Refusal is { } refusal)
        {
            return Refused(candidate, refusal);
        }

        var verified = reading.Candidates.Where(path => Verifies(path, candidate.ReportedSize)).ToList();
        if (PathCandidateGuard.SingleVerified(verified) is not { } path)
        {
            return Refused(
                candidate,
                verified.Count == 0
                    ? ImportOutcome.RefusedNotFound
                    : ImportOutcome.RefusedAmbiguous,
                verified.Count);
        }

        var imported = await library.ImportVideoAsync(path, ct).ConfigureAwait(false);
        if (!imported.Reached)
        {
            return Refused(candidate, ImportOutcome.RefusedHostImportUnavailable);
        }

        return ImportOutcome.Imported;
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> is the one the delivery described.
    /// </summary>
    /// <remarks>
    /// The size is compared when the delivery reported one. A file of the right name and a different
    /// length is a different file, and both generations report a size, so accepting on the name
    /// alone would give up a check that is always available.
    /// </remarks>
    private bool Verifies(string path, long? reportedSize)
    {
        var probed = paths.Probe(path);
        return probed.Exists && (reportedSize is null || probed.Size == reportedSize);
    }

    private ImportOutcome Refused(ImportCandidate candidate, PathCandidateRefusal refusal)
        => Refused(
            candidate,
            refusal switch
            {
                PathCandidateRefusal.NoReportedPath => ImportOutcome.RefusedUnreadablePayload,
                PathCandidateRefusal.NoReportedRoots => ImportOutcome.RefusedNoReportedRoots,
                PathCandidateRefusal.PathOutsideEveryReportedRoot
                    => ImportOutcome.RefusedPathOutsideEveryReportedRoot,
                PathCandidateRefusal.NoLibraryRoots => ImportOutcome.RefusedNoLibraryRoots,
                PathCandidateRefusal.EveryCandidateEscapedItsRoot => ImportOutcome.RefusedNotFound,
                _ => ImportOutcome.RefusedUnreadablePayload,
            });

    // Logged where the outcome is decided rather than at each return, so every refusal is reported
    // once and none can be added without one. No path is named: the log is durable and readable, and
    // a refused delivery's path is a caller-supplied string.
    private ImportOutcome Refused(ImportCandidate candidate, ImportOutcome outcome, int verified = 0)
    {
        WhisparrSyncLog.ImportRefused(log, candidate.Generation, outcome, verified);
        return outcome;
    }
}
