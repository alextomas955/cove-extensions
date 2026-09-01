using Microsoft.Extensions.Logging;
using WhisparrSync.Options;

namespace WhisparrSync.Import;

/// <inheritdoc cref="IImportCore"/>
internal sealed class ImportCore(
    IReportedRootPort reportedRoots,
    ICoveLibraryPort library,
    IImportPathPort paths,
    OptionsStore options,
    FollowUpScanCoalescer followUp,
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

        var identity = await IdentifyAsync(candidate, ct).ConfigureAwait(false);
        if (identity is { Resolution.Ambiguous: true })
        {
            return await RefusedAsync(
                candidate, reading, ImportOutcome.RefusedAmbiguousIdentity, null, ct)
                .ConfigureAwait(false);
        }

        // The live dedupe, and the whole of what makes the two channels ingest one file once between
        // them. It is DERIVED on every delivery: nothing per-file, per-scene or per-delivery is kept,
        // so no state of this extension's can disagree with the library.
        //
        // The host's own lookup by parent folder and basename returns the existing row rather than
        // creating a video, and it gates neither the enrichment nor the follow-up. Those are this
        // check's to decide.
        // The one argument that separates a second item from the same item now holding two files.
        // Move detection is off on the host's import path, so a byte-identical file at a new path
        // creates a new item unless the item the identifier named is passed deliberately.
        var repointedTo = identity?.Resolution.VideoId;

        if (await library.HeldFileAtAsync(path, ct).ConfigureAwait(false) is { } row)
        {
            if (row.VideoId is { } held)
            {
                // The identity is still written where the item carries none: the channel that arrives
                // first may be the one that reads no identifier at all.
                if (identity is { } carried)
                {
                    await StampAndEnrichAsync(carried, held, ct).ConfigureAwait(false);
                }

                return ImportOutcome.AlreadyHeld;
            }

            // A row the Replace behaviour left behind. Whether the delivery resolved an identity is
            // what separates a re-attachment from a file this product cannot place: the host attaches
            // the row to the item it is handed, and handed none it leaves the key unset and raises.
            if (repointedTo is null)
            {
                return await RefusedAsync(
                    candidate, reading, ImportOutcome.RefusedDetachedFileWithoutIdentity, null, ct)
                    .ConfigureAwait(false);
            }
        }

        var imported = await library.ImportVideoAsync(path, repointedTo, ct).ConfigureAwait(false);
        if (!imported.Reached)
        {
            return await RefusedAsync(
                candidate,
                reading,
                ImportOutcome.RefusedHostImportUnavailable,
                ImportRefusalCause.Unreadable,
                ct).ConfigureAwait(false);
        }

        if (repointedTo is { } upgraded)
        {
            await DetachSupersededAsync(upgraded, path, ct).ConfigureAwait(false);
        }

        if (identity is { } named && imported.VideoId is { } item)
        {
            await StampAndEnrichAsync(named, item, ct).ConfigureAwait(false);
        }

        // Only a delivery that registered a file clears the root's outstanding refusals, and only one
        // is covered by a follow-up. A refusal is not an import.
        followUp.NoteImported(path, library);
        await ClearAsync(reading.RefusalRoot, ct).ConfigureAwait(false);
        return ImportOutcome.Imported;
    }

    /// <summary>Detaches the rows this upgrade superseded, under the behaviour that asks for it.</summary>
    /// <remarks>
    /// Only the row's video key is cleared. The superseded file on disk is not touched, in either
    /// system's storage, and remains Whisparr's to remove.
    /// </remarks>
    private async Task DetachSupersededAsync(int videoId, string keptPath, CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        if (stored.UpgradeBehavior != UpgradeBehavior.Replace)
        {
            return;
        }

        await library.DetachSupersededFilesAsync(videoId, keptPath, ct).ConfigureAwait(false);
    }

    /// <summary>What this delivery's identifier resolves to, or null when it carried none.</summary>
    /// <remarks>
    /// A delivery with no identifier stamps nothing and enriches nothing, and the item is still
    /// created: an identity is what a scene may later be matched on, not what makes it importable.
    /// </remarks>
    private async Task<DeliveredIdentity?> IdentifyAsync(
        ImportCandidate candidate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(candidate.RemoteId))
        {
            return null;
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var endpoint = IdentityEndpoint.Resolve(
            candidate.Generation,
            stored.MetadataProviderEndpoints,
            library.ConfiguredMetadataEndpoints);

        return new DeliveredIdentity(
            endpoint,
            candidate.RemoteId,
            await library.ResolveByRemoteIdAsync(endpoint, candidate.RemoteId, ct).ConfigureAwait(false));
    }

    /// <summary>Stamps the identity row, and enriches only where there was none to begin with.</summary>
    /// <remarks>
    /// Enrichment happens at most once per scene, and the gate is that the item carried no row for the
    /// source before this delivery. The host's merge with no import configuration overwrites its
    /// scalar fields, and that call takes no configuration which would make a second application safe.
    /// <para>
    /// There is no backfill. A scene stamped while no source was configured stays bare until the user
    /// asks for it: configuring a source later triggers nothing, and a redelivery over that scene
    /// enriches nothing.
    /// </para>
    /// </remarks>
    private async Task StampAndEnrichAsync(
        DeliveredIdentity identity, int videoId, CancellationToken ct)
    {
        if (await library.CarriesIdentityAsync(videoId, identity.Endpoint, ct).ConfigureAwait(false))
        {
            return;
        }

        await library.StampIdentityAsync(videoId, identity.Endpoint, identity.RemoteId, ct)
            .ConfigureAwait(false);

        try
        {
            await library.EnrichAsync(videoId, identity.Endpoint, identity.RemoteId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Above the broad catch, so a shutdown classifies as cancelled rather than as a failure.
            throw;
        }
#pragma warning disable CA1031 // Best-effort by the host's own documented contract.
        catch (Exception failure)
        {
            WhisparrSyncLog.EnrichmentContained(log, identity.Source, failure);
        }
#pragma warning restore CA1031
    }

    /// <summary>The identity one delivery names, and what the library says it points at.</summary>
    private sealed record DeliveredIdentity(
        string Endpoint, string RemoteId, IdentityResolution Resolution)
    {
        /// <summary>The source's registrable domain, which is all a log line is given of it.</summary>
        public string Source { get; } = EndpointMatchGuard.RegistrableDomain(Endpoint);
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
