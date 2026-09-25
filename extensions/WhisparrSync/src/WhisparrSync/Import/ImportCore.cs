using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Identity;
using WhisparrSync.Options;

namespace WhisparrSync.Import;

internal sealed class ImportCore(
    IReportedRootPort reportedRoots,
    ICoveLibraryPort library,
    IImportPathPort paths,
    OptionsWriting writing,
    FollowUpScanCoalescer followUp,
    TimeProvider clock,
    ILogger log) : IImportCore
{
    public async Task<ImportOutcome> IngestAsync(ImportCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var reading = PathCandidateGuard.Read(
            candidate.ReportedPath,
            await reportedRoots.ReadAsync(candidate.Generation, ct).ConfigureAwait(false) ?? [],
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

        // The dedupe that makes the two channels ingest one file once between them, derived on
        // every delivery: nothing per file, per scene or per delivery is kept, so no state of this
        // extension's can disagree with the library.
        //
        // Move detection is off on the host's import path, so a byte-identical file at a new path
        // creates a second item unless the item the identifier named is passed here deliberately.
        var repointedTo = identity?.Resolution.VideoId;

        if (await AlreadyHeldAsync(candidate, reading, path, identity, repointedTo, ct)
                .ConfigureAwait(false) is { } settled)
        {
            return settled;
        }

        var imported = await library.ImportVideoAsync(path, repointedTo, ct).ConfigureAwait(false);
        if (await HostRefusedAsync(candidate, reading, imported, ct).ConfigureAwait(false)
            is { } declined)
        {
            return declined;
        }

        if (repointedTo is { } upgraded)
        {
            await DetachSupersededAsync(upgraded, path, ct).ConfigureAwait(false);
        }

        if (identity is { } named && imported.VideoId is { } item)
        {
            await StampAndEnrichAsync(named, item, ct).ConfigureAwait(false);
        }

        followUp.NoteImported(path, library);
        await RecordImportedAsync(candidate.Generation, reading.RefusalRoot, ct).ConfigureAwait(false);
        return ImportOutcome.Imported;
    }

    // Only the row's video key is cleared. The superseded file on disk is not touched and remains
    // Whisparr's to remove.
    private async Task DetachSupersededAsync(int videoId, string keptPath, CancellationToken ct)
    {
        var stored = await writing.Store.LoadAsync(ct).ConfigureAwait(false);
        if (stored.UpgradeBehavior != UpgradeBehavior.Replace)
        {
            return;
        }

        await library.DetachSupersededFilesAsync(videoId, keptPath, ct).ConfigureAwait(false);
    }

    // Null when the delivery carried no identifier. Such a delivery stamps and enriches nothing,
    // and the item is still created.
    private async Task<DeliveredIdentity?> IdentifyAsync(
        ImportCandidate candidate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(candidate.RemoteId))
        {
            return null;
        }

        var stored = await writing.Store.LoadAsync(ct).ConfigureAwait(false);
        var endpoint = IdentityEndpoint.Resolve(
            candidate.Generation,
            stored.MetadataProviderEndpoints,
            library.ConfiguredMetadataEndpoints);

        return new DeliveredIdentity(
            endpoint,
            candidate.RemoteId,
            await library.ResolveByRemoteIdAsync(endpoint, candidate.RemoteId, ct).ConfigureAwait(false));
    }

    // Enrichment happens at most once per scene, gated on the item carrying no row for the source
    // before this delivery. The host's merge with no import configuration overwrites its scalar
    // fields, and no configuration at that call would make a second application safe.
    // There is no backfill: a scene stamped while no source was configured stays bare, and a
    // redelivery over it enriches nothing.
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
        catch (EnrichmentNotCommittedException failure)
        {
            WhisparrSyncLog.EnrichmentNotCommitted(log, identity.Source, failure);
        }
#pragma warning disable CA1031 // Best-effort by the host's own documented contract.
        catch (Exception failure)
        {
            // A local rather than an argument: CA1873 reads a call in the argument of a line whose
            // level may be disabled as work that should not be done.
            var classified = WhisparrSyncLog.Classify(failure);
            WhisparrSyncLog.EnrichmentContained(log, identity.Source, classified);
        }
#pragma warning restore CA1031
    }

    private sealed record DeliveredIdentity(
        string Endpoint, string RemoteId, IdentityResolution Resolution)
    {
        // The registrable domain, which is all a log line is given of the source.
        public string Source { get; } = EndpointMatchGuard.RegistrableDomain(Endpoint);
    }

    private static ImportOutcome OutcomeOf(ImportRefusalCause? cause)
        => cause switch
        {
            ImportRefusalCause.NotFoundUnderAnyRoot => ImportOutcome.RefusedNotFound,
            ImportRefusalCause.AmbiguousCandidates => ImportOutcome.RefusedAmbiguous,
            ImportRefusalCause.Unreadable => ImportOutcome.RefusedHostRefusedFile,
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

    // Null where the refusal is not counted against a root: one with no offending path to list, or
    // one whose fault is the host's own configuration rather than a Whisparr root.
    private static ImportRefusalCause? RecordedCauseOf(PathCandidateRefusal refusal)
        => refusal switch
        {
            PathCandidateRefusal.PathOutsideEveryReportedRoot
                or PathCandidateRefusal.EveryCandidateEscapedItsRoot
                => ImportRefusalCause.NotFoundUnderAnyRoot,
            _ => null,
        };

    // Logged where the outcome is decided rather than at each return, so every refusal is reported
    // once. The root is logged and not the offending path, which is a caller-supplied string.
    // What the library already holds at this path, or null where it holds nothing there and the
    // ingest carries on.
    private async Task<ImportOutcome?> AlreadyHeldAsync(
        ImportCandidate candidate,
        PathCandidateReading reading,
        string path,
        DeliveredIdentity? identity,
        int? repointedTo,
        CancellationToken ct)
    {
        if (await library.HeldFileAtAsync(path, ct).ConfigureAwait(false) is not { } row)
        {
            return null;
        }

        if (row.VideoId is { } held)
        {
            // The identity is still written where the item carries none: the channel that arrives
            // first may be the one that reads no identifier.
            if (identity is { } carried)
            {
                await StampAndEnrichAsync(carried, held, ct).ConfigureAwait(false);
            }

            // The file came from this root and is in the library, so the root is working. The
            // follow-up covers the item in case the delivery that registered it was interrupted
            // after the host committed and before it could be noted.
            followUp.NoteImported(path, library);
            await ClearAsync(candidate.Generation, reading.RefusalRoot, ct).ConfigureAwait(false);
            return ImportOutcome.AlreadyHeld;
        }

        // A row the Replace behaviour left behind. The host attaches the row to the item it is
        // handed; handed none it leaves the key unset and raises, so a delivery with no resolved
        // identity is refused here.
        return repointedTo is null
            ? await RefusedAsync(
                    candidate, reading, ImportOutcome.RefusedDetachedFileWithoutIdentity, null, ct)
                .ConfigureAwait(false)
            : null;
    }

    // An import the container could not produce is counted against no root: nothing about it is a
    // Whisparr root the user misconfigured. A file the host declined is counted against the root,
    // because the path came from there.
    private async Task<ImportOutcome?> HostRefusedAsync(
        ImportCandidate candidate,
        PathCandidateReading reading,
        LibraryImport imported,
        CancellationToken ct)
        => imported.Outcome switch
        {
            LibraryImportOutcome.ServiceUnavailable => await RefusedAsync(
                    candidate, reading, ImportOutcome.RefusedHostImportUnavailable, null, ct)
                .ConfigureAwait(false),
            LibraryImportOutcome.HostRefused => await RefusedAsync(
                    candidate,
                    reading,
                    ImportOutcome.RefusedHostRefusedFile,
                    ImportRefusalCause.Unreadable,
                    ct)
                .ConfigureAwait(false),
            _ => null,
        };

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
            await RecordAsync(candidate.Generation, reading.RefusalRoot, candidate.ReportedPath, recorded, ct)
                .ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task RecordAsync(
        WhisparrGeneration generation,
        string root,
        string path,
        ImportRefusalCause cause,
        CancellationToken ct)
        => await writing.Gate.MutateAsync(
            writing.Store,
            stored => WithRefusals(
                stored,
                generation,
                ImportRefusalProjector.Refuse(
                    stored.InstanceSettingsOrEmptyFor(generation).ImportRefusals, root, path, cause)),
            ct).ConfigureAwait(false);

    // No member of the health aggregate is touched: the caller reached this without registering a
    // file, and a delivery whose file was already there is not an import.
    private async Task ClearAsync(
        WhisparrGeneration generation, string root, CancellationToken ct)
        => await writing.Gate.MutateAsync(
            writing.Store,
            stored => WithRefusals(
                stored,
                generation,
                ImportRefusalProjector.Succeed(
                    stored.InstanceSettingsOrEmptyFor(generation).ImportRefusals, root)),
            ct).ConfigureAwait(false);

    // The instant is taken before the writing.Gate, so it records when the file was registered rather than
    // when the lock came free. This is its only writer: the live channel imports with no pass
    // running, so a member the pass wrote would read as never against a working webhook.
    private async Task RecordImportedAsync(
        WhisparrGeneration generation, string root, CancellationToken ct)
    {
        var workedAt = clock.GetUtcNow();
        await writing.Gate.MutateAsync(
            writing.Store,
            stored => WithRefusals(
                stored,
                generation,
                ImportRefusalProjector.Succeed(
                    stored.InstanceSettingsOrEmptyFor(generation).ImportRefusals, root))
                with
            {
                ImportHealth = stored.ImportHealth with { LastWorkedAtUtc = workedAt },
            },
            ct).ConfigureAwait(false);
    }

    private static WhisparrSyncOptions WithRefusals(
        WhisparrSyncOptions stored,
        WhisparrGeneration generation,
        List<ImportRootRefusals> refusals)
        => stored.WithInstanceSettingsFor(
            generation,
            stored.InstanceSettingsOrEmptyFor(generation) with { ImportRefusals = refusals });
}
