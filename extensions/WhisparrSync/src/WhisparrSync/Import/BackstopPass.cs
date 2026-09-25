using System.Text.Json.Nodes;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Import;

internal sealed class BackstopPass(
    WhisparrAccess whisparr,
    OptionsWriteGate gate,
    IImportCore core,
    TimeProvider clock,
    FollowUpScanCoalescer followUp,
    ICoveLibraryPort library) : IBackstopPass
{
    // Caps one response, not the walk. The walk's bound is the stored mark: capping the page count
    // would drop history the mark says is unread and then move the mark past it.
    internal const int PageSize = 50;

    public async Task<BackstopPassResult> RunAsync(CancellationToken ct)
    {
        var stored = await whisparr.Options.LoadAsync(ct).ConfigureAwait(false);
        var generation = stored.SelectedGeneration;
        var connection = stored.ConnectionFor(generation);
        var binding = await OutboundPair.ResolveAsync(stored, whisparr.Credentials, ct).ConfigureAwait(false);
        if (binding is null)
        {
            return new BackstopPassResult(BackstopPassOutcome.NotConfigured, null, 0, 0, 0, 0, 0);
        }

        // The identity the walk is recorded under is the one it contacted, not the one the blob
        // describes, so the two cannot disagree. It is captured before the walk rather than read
        // back after it: a save committed while the walk runs moves the stored address, and the fold
        // has to know whether the record is still this instance.
        var walkedAddress = binding.BaseAddress.ToString();

        // The stored mark is a position in the history of the instance the blob names. Where the row
        // names another, the walk starts with no position rather than declaring that instance's
        // older records read; it reaches one page, records nothing and reports the position lost.
        var walkedElsewhere = !ConnectionTester.IsSameAddress(connection?.Address, walkedAddress);

        var walk = await WalkAsync(
            binding,
            walkedElsewhere ? null : connection?.BackstopWatermarkUtc,
            ct)
            .ConfigureAwait(false);

        // A refused pass leaves the mark where it was. Moving it over pages the walk declined to
        // read would skip those records for good.
        if (walk.Outcome == BackstopPassOutcome.FirstConnect)
        {
            // A mark is written even against an instance with no history, or every later pass is
            // another first connect. Records before this mark are never replayed, so the
            // position-lost flag is raised in the same fold that clears the failures.
            await RecordWalkedAsync(
                generation,
                walkedAddress,
                walk.Watermark ?? clock.GetUtcNow(),
                positionLost: true,
                walk.Contained,
                ct)
                .ConfigureAwait(false);
        }
        else if (walk.Outcome == BackstopPassOutcome.Walked)
        {
            // Recorded even with no watermark to write. The pass still reached, authenticated
            // against and read the instance, which is what the health half reports; the mark stays
            // where it was.
            await RecordWalkedAsync(
                generation, walkedAddress, walk.Watermark, positionLost: false, walk.Contained, ct)
                .ConfigureAwait(false);
        }
        else if (IsRefusal(walk.Outcome))
        {
            WhisparrSyncLog.BackstopPassRefused(
                whisparr.Log, generation, walk.Outcome, binding.BaseAddress.Host);
            await RecordFailureAsync(walk.Outcome, ct).ConfigureAwait(false);
        }

        // The pass boundary is a batch boundary: one scan covers whatever this walk imported.
        followUp.Flush(library);
        return walk;
    }

    private static bool IsRefusal(BackstopPassOutcome outcome)
        => outcome is BackstopPassOutcome.RefusedPageOrder
            or BackstopPassOutcome.RefusedUnreadableAnswer
            or BackstopPassOutcome.RefusedUnreachable;

    // Nothing held across an iteration grows with the walk: counters, instants, and one page's ids.
    private async Task<BackstopPassResult> WalkAsync(
        WhisparrBinding binding,
        DateTimeOffset? mark,
        CancellationToken ct)
    {
        var instance = whisparr.Instances.Bound(binding);
        var tally = new WalkTally();
        var page = 1;
        DateTimeOffset? newest = null;
        DateTimeOffset? previousPageOldest = null;
        DateTimeOffset? previousPageNewest = null;

        // Replaced by each page, never appended to, so it holds one page's ids however far the walk
        // reads.
        IReadOnlyList<string>? previousPageIds = null;

        while (true)
        {
            var read = await ReadHistoryPageAsync(instance, page, ct).ConfigureAwait(false);
            if (read is not { Records: { } records, Instants: { } instants })
            {
                return Ended(read.Refusal ?? BackstopPassOutcome.RefusedUnreadableAnswer);
            }

            var ids = HistoryProjector.IdsIn(records);
            var reading = WatermarkGuard.Read(
                instants, mark, newest, previousPageOldest, previousPageNewest, ids, previousPageIds);
            newest = reading.Newest;
            if (reading.Refused)
            {
                return Ended(BackstopPassOutcome.RefusedPageOrder);
            }

            await IngestPageAsync(binding, records, reading, tally, ct).ConfigureAwait(false);

            if (!reading.Continue || records.Count < PageSize)
            {
                return Ended(
                    mark is null ? BackstopPassOutcome.FirstConnect : BackstopPassOutcome.Walked);
            }

            previousPageOldest = instants[^1];
            previousPageNewest = reading.PageNewest;
            previousPageIds = ids;
            page++;
        }

        BackstopPassResult Ended(BackstopPassOutcome outcome)
            => new(
                outcome,
                IsRefusal(outcome) ? null : newest,
                page,
                tally.Taken,
                tally.Imported,
                tally.WithoutCandidate,
                tally.Contained);
    }

    // One page of history, or the refusal that stands in its place. Records and Instants are both
    // set exactly when Refusal is null.
    private readonly record struct HistoryPage(
        BackstopPassOutcome? Refusal, JsonArray? Records, IReadOnlyList<DateTimeOffset>? Instants);

    private static async Task<HistoryPage> ReadHistoryPageAsync(
        IWhisparrClient instance, int page, CancellationToken ct)
    {
        if (await ReadPageAsync(instance, page, ct).ConfigureAwait(false) is not { } answer)
        {
            return new HistoryPage(BackstopPassOutcome.RefusedUnreachable, null, null);
        }

        return HistoryProjector.RecordsIn(answer.Body) is { } records
            && HistoryProjector.InstantsIn(records) is { } instants
                ? new HistoryPage(null, records, instants)
                : new HistoryPage(BackstopPassOutcome.RefusedUnreadableAnswer, null, null);
    }

    // Null where the instance could not be reached, which the walk reports as a refusal rather than
    // raising: a pass that cannot read is not a pass that failed.
    private static async Task<WhisparrResponse?> ReadPageAsync(
        IWhisparrClient instance, int page, CancellationToken ct)
    {
        try
        {
            return await instance.ReadHistoryAsync(page, PageSize, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Above the broad catch, so a shutdown classifies as cancelled rather than as a failed
            // pass.
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task IngestPageAsync(
        WhisparrBinding binding,
        JsonArray records,
        WatermarkReading reading,
        WalkTally tally,
        CancellationToken ct)
    {
        for (var index = reading.Skip; index < reading.Skip + reading.Take; index++)
        {
            tally.Taken++;
            switch (HistoryProjector.Read(binding.Generation, records[index] as JsonObject))
            {
                case { Outcome: HistoryProjectionOutcome.Projected, Candidate: { } candidate }:
                    await IngestRecordAsync(binding, candidate, tally, ct).ConfigureAwait(false);
                    break;

                case { Outcome: HistoryProjectionOutcome.NoReadablePath }:
                    tally.WithoutCandidate++;
                    break;

                default:
                    break;
            }
        }
    }

    // Guarded per record: one record the ingest cannot take must not stop the mark being written. A
    // walk that ends in a throw leaves the mark where it was, so every later pass reads the same
    // page and throws again.
    private async Task IngestRecordAsync(
        WhisparrBinding binding, ImportCandidate candidate, WalkTally tally, CancellationToken ct)
    {
        try
        {
            if (await core.IngestAsync(candidate, ct).ConfigureAwait(false)
                == ImportOutcome.Imported)
            {
                tally.Imported++;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Above the broad catch, so a shutdown classifies as cancelled.
            throw;
        }
#pragma warning disable CA1031 // The point of the guard is that no failure ends the walk.
        catch (Exception failure)
        {
            WhisparrSyncLog.BackstopRecordContained(
                whisparr.Log, binding.Generation, WhisparrSyncLog.Classify(failure));
            tally.Contained++;
        }
#pragma warning restore CA1031
    }

    // Counters only, so nothing here grows with the walk.
    private sealed class WalkTally
    {
        internal int Taken { get; set; }

        internal int Imported { get; set; }

        internal int WithoutCandidate { get; set; }

        internal int Contained { get; set; }
    }

    // One fold, so the mark and the health cannot be written against two readings of the blob, and
    // folded onto whatever the gate loads: the ingest core writes the refusal aggregate to the same
    // blob while the walk runs. The last-failed instant is left alone, as it is the only record of
    // when a failure happened. Contained records are added to the stored total, never replacing it.
    // The mark is refused when the stored record no longer names the walked address, because it
    // would then name a position in a different instance's history; the health half is written
    // either way.
    private async Task RecordWalkedAsync(
        WhisparrGeneration generation,
        string walkedAddress,
        DateTimeOffset? mark,
        bool positionLost,
        int contained,
        CancellationToken ct)
    {
        var containedAt = contained == 0 ? null : (DateTimeOffset?)clock.GetUtcNow();
        await gate.MutateAsync(
            whisparr.Options,
            stored => Marked(stored, generation, walkedAddress, mark) with
            {
                ImportHealth = stored.ImportHealth with
                {
                    ConsecutiveFailures = 0,
                    LastError = "",
                    BackstopPositionLost = positionLost,
                    RecordsContained = stored.ImportHealth.RecordsContained + contained,
                    LastContainedAtUtc = containedAt ?? stored.ImportHealth.LastContainedAtUtc,
                },
            },
            ct).ConfigureAwait(false);
    }

    private static WhisparrSyncOptions Marked(
        WhisparrSyncOptions stored,
        WhisparrGeneration generation,
        string walkedAddress,
        DateTimeOffset? mark)
        => mark is { } reached
            && stored.ConnectionFor(generation) is { } connection
            && ConnectionTester.IsSameAddress(connection.Address, walkedAddress)
                ? stored.WithConnectionFor(generation, connection with { BackstopWatermarkUtc = reached })
                : stored;

    private async Task RecordFailureAsync(BackstopPassOutcome outcome, CancellationToken ct)
    {
        var failedAt = clock.GetUtcNow();
        await gate.MutateAsync(
            whisparr.Options,
            stored => stored with
            {
                ImportHealth = stored.ImportHealth with
                {
                    LastFailedAtUtc = failedAt,
                    LastError = outcome.ToString(),
                    ConsecutiveFailures = stored.ImportHealth.ConsecutiveFailures + 1,
                },
            },
            ct).ConfigureAwait(false);
    }
}
