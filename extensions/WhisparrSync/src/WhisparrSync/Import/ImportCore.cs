using Microsoft.Extensions.Logging;
using WhisparrSync.Options;

namespace WhisparrSync.Import;

/// <inheritdoc cref="IImportCore"/>
internal sealed class ImportCore(
    IReportedRootPort reportedRoots,
    ICoveLibraryPort library,
    IImportPathPort paths,
    OptionsStore options,
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
            return await RefusedAsync(candidate, reading, OutcomeOf(refusal), RecordedCauseOf(refusal), ct)
                .ConfigureAwait(false);
        }

        var resolution = PathCandidateGuard.Resolve(
            [.. reading.Candidates.Select(path => new ProbedCandidate(path, paths.Probe(path)))],
            candidate.ReportedSize);
        if (resolution.Path is not { } path)
        {
            return await RefusedAsync(
                candidate, reading, OutcomeOf(resolution.Cause), resolution.Cause, ct)
                .ConfigureAwait(false);
        }

        var imported = await library.ImportVideoAsync(path, null, ct).ConfigureAwait(false);
        if (!imported.Reached)
        {
            return await RefusedAsync(
                candidate,
                reading,
                ImportOutcome.RefusedHostImportUnavailable,
                ImportRefusalCause.Unreadable,
                ct).ConfigureAwait(false);
        }

        await ClearAsync(reading.RefusalRoot, ct).ConfigureAwait(false);
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

    private static ImportOutcome OutcomeOf(PathCandidateRefusal refusal)
        => refusal switch
        {
            PathCandidateRefusal.NoReportedPath => ImportOutcome.RefusedUnreadablePayload,
            PathCandidateRefusal.NoReportedRoots => ImportOutcome.RefusedNoReportedRoots,
            PathCandidateRefusal.PathOutsideEveryReportedRoot
                => ImportOutcome.RefusedPathOutsideEveryReportedRoot,
            PathCandidateRefusal.NoLibraryRoots => ImportOutcome.RefusedNoLibraryRoots,
            PathCandidateRefusal.EveryCandidateEscapedItsRoot => ImportOutcome.RefusedNotFound,
            _ => ImportOutcome.RefusedUnreadablePayload,
        };

    /// <summary>The banner cause a pre-probe refusal is counted under, or null when it is not one.</summary>
    /// <remarks>
    /// A refusal with no offending path to list, or one whose fault lies with the host's own
    /// configuration rather than with a Whisparr root, is reported in the log and not counted against
    /// a root: a line under a root the user did not misconfigure sends them to the wrong place.
    /// </remarks>
    private static ImportRefusalCause? RecordedCauseOf(PathCandidateRefusal refusal)
        => refusal switch
        {
            PathCandidateRefusal.PathOutsideEveryReportedRoot
                or PathCandidateRefusal.EveryCandidateEscapedItsRoot
                => ImportRefusalCause.NotFoundUnderAnyRoot,
            _ => null,
        };

    // Logged where the outcome is decided rather than at each return, so every refusal is reported
    // once and none can be added without one. The root rather than the offending path: the log is
    // durable and readable, and a refused delivery's path is a caller-supplied string.
    private async Task<ImportOutcome> RefusedAsync(
        ImportCandidate candidate,
        PathCandidateReading reading,
        ImportOutcome outcome,
        ImportRefusalCause? cause,
        CancellationToken ct)
    {
        WhisparrSyncLog.ImportRefused(log, candidate.Generation, outcome, reading.RefusalRoot);

        if (cause is { } recorded)
        {
            await RecordAsync(reading.RefusalRoot, candidate.ReportedPath, recorded, ct)
                .ConfigureAwait(false);
        }

        return outcome;
    }

    // Read-modify-write over the one stored blob, and written only when the fold changed it: a
    // delivery stream is per FILE, and a save per delivery would write the whole blob for every one.
    private async Task RecordAsync(
        string root, string path, ImportRefusalCause cause, CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        await SaveIfChangedAsync(
            stored,
            stored with { ImportRefusals = ImportRefusalProjector.Refuse(stored.ImportRefusals, root, path, cause) },
            ct).ConfigureAwait(false);
    }

    private async Task ClearAsync(string root, CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        await SaveIfChangedAsync(
            stored,
            stored with { ImportRefusals = ImportRefusalProjector.Succeed(stored.ImportRefusals, root) },
            ct).ConfigureAwait(false);
    }

    private async Task SaveIfChangedAsync(
        WhisparrSyncOptions stored, WhisparrSyncOptions next, CancellationToken ct)
    {
        if (next != stored)
        {
            await options.SaveAsync(next, ct).ConfigureAwait(false);
        }
    }
}
