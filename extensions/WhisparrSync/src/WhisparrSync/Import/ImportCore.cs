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

        var resolution = PathCandidateGuard.Resolve(
            [.. reading.Candidates.Select(path => new ProbedCandidate(path, paths.Probe(path)))],
            candidate.ReportedSize);
        if (resolution.Path is not { } path)
        {
            return Refused(candidate, OutcomeOf(resolution.Cause));
        }

        var imported = await library.ImportVideoAsync(path, ct).ConfigureAwait(false);
        if (!imported.Reached)
        {
            return Refused(candidate, ImportOutcome.RefusedHostImportUnavailable);
        }

        return ImportOutcome.Imported;
    }

    private static ImportOutcome OutcomeOf(ImportRefusalCause? cause)
        => cause switch
        {
            ImportRefusalCause.NotFoundUnderAnyRoot => ImportOutcome.RefusedNotFound,
            ImportRefusalCause.AmbiguousCandidates => ImportOutcome.RefusedAmbiguous,
            ImportRefusalCause.Unreadable => ImportOutcome.RefusedHostImportUnavailable,
            _ => ImportOutcome.RefusedUnreadablePayload,
        };

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
    private ImportOutcome Refused(ImportCandidate candidate, ImportOutcome outcome)
    {
        WhisparrSyncLog.ImportRefused(log, candidate.Generation, outcome);
        return outcome;
    }
}
