using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Import;

internal sealed class BackstopPass(
    IWhisparrClient client,
    OptionsStore options,
    OptionsWriteGate gate,
    ICredentialPort credentials,
    IImportCore core,
    TimeProvider clock,
    FollowUpScanCoalescer followUp,
    ICoveLibraryPort library,
    ILogger log) : IBackstopPass
{
    // Caps one response, not the walk. The walk's bound is the stored mark: capping the page count
    // would drop history the mark says is unread and then move the mark past it.
    internal const int PageSize = 50;

    public async Task<BackstopPassResult> RunAsync(CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var generation = stored.SelectedGeneration;
        var connection = stored.ConnectionFor(generation);
        var held = await credentials.ReadConnectionAsync(generation, ct).ConfigureAwait(false);
        var apiKey = held?.ApiKey;
        // The address comes from the row that holds the key, so the two cannot be observed from
        // either side of a save that changed both. A row written before the address was stored there
        // carries none, and the stored options answer for that installation until its next save.
        var address = string.IsNullOrWhiteSpace(held?.Address) ? connection?.Address : held.Address;

        // Refused here, so an unconfigured connection reaches nothing that could make a request.
        if (!ConnectionTester.TryReadConnection(address, apiKey, out var baseAddress, out _))
        {
            return new BackstopPassResult(BackstopPassOutcome.NotConfigured, null, 0, 0, 0, 0, 0);
        }

        // Captured before the walk, not read back after it: a save committed while the walk runs
        // moves the stored address, and the fold has to know whether the record is still this
        // instance.
        var walkedAddress = connection!.Address;

        var walk = await WalkAsync(
            new WhisparrBinding(generation, baseAddress, apiKey),
            connection.BackstopWatermarkUtc,
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
            WhisparrSyncLog.BackstopPassRefused(log, generation, walk.Outcome, baseAddress.Host);
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
        var generation = binding.Generation;

        var page = 1;
        var taken = 0;
        var imported = 0;
        var withoutCandidate = 0;
        var contained = 0;
        DateTimeOffset? newest = null;
        DateTimeOffset? previousPageOldest = null;
        DateTimeOffset? previousPageNewest = null;

        // Replaced by each page, never appended to, so it holds one page's ids however far the walk
        // reads.
        IReadOnlyList<string>? previousPageIds = null;

        while (true)
        {
            WhisparrResponse answer;
            try
            {
                answer = await client
                    .ReadHistoryAsync(
                        binding.BaseAddress, binding.ApiKey, generation, page, PageSize, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Above the broad catch, so a shutdown classifies as cancelled rather than as a
                // failed pass.
                throw;
            }
            catch (Exception failure)
                when (failure is HttpRequestException or IOException or TaskCanceledException)
            {
                return Ended(BackstopPassOutcome.RefusedUnreachable);
            }

            if (HistoryProjector.RecordsIn(answer.Body) is not { } records)
            {
                return Ended(BackstopPassOutcome.RefusedUnreadableAnswer);
            }

            if (HistoryProjector.InstantsIn(records) is not { } instants)
            {
                return Ended(BackstopPassOutcome.RefusedUnreadableAnswer);
            }

            var ids = HistoryProjector.IdsIn(records);
            var reading = WatermarkGuard.Read(
                instants, mark, newest, previousPageOldest, previousPageNewest, ids, previousPageIds);
            newest = reading.Newest;
            if (reading.Refused)
            {
                return Ended(BackstopPassOutcome.RefusedPageOrder);
            }

            for (var index = reading.Skip; index < reading.Skip + reading.Take; index++)
            {
                taken++;
                switch (HistoryProjector.Read(generation, records[index] as JsonObject))
                {
                    case { Outcome: HistoryProjectionOutcome.Projected, Candidate: { } candidate }:
                        // Guarded per record: one record the ingest cannot take must not stop the
                        // mark being written. A walk that ends in a throw leaves the mark where it
                        // was, so every later pass reads the same page and throws again.
                        try
                        {
                            if (await core.IngestAsync(candidate, ct).ConfigureAwait(false)
                                == ImportOutcome.Imported)
                            {
                                imported++;
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
                                log, generation, WhisparrSyncLog.Classify(failure));
                            contained++;
                        }
#pragma warning restore CA1031

                        break;

                    case { Outcome: HistoryProjectionOutcome.NoReadablePath }:
                        withoutCandidate++;
                        break;

                    default:
                        break;
                }
            }

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
                taken,
                imported,
                withoutCandidate,
                contained);
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
            options,
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
            options,
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
