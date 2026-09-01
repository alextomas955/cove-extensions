using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Import;

/// <inheritdoc cref="IBackstopPass"/>
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
    /// <summary>How many records one page asks for.</summary>
    /// <remarks>
    /// The walk's bound is the stored mark, not this. A page size caps what one response has to hold;
    /// capping the number of pages would drop history the mark says has not been read, and then move
    /// the mark past it.
    /// </remarks>
    internal const int PageSize = 50;

    public async Task<BackstopPassResult> RunAsync(CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var generation = stored.SelectedGeneration;
        var connection = stored.ConnectionFor(generation);
        var apiKey = await credentials.ReadAsync(generation, ct).ConfigureAwait(false);

        // Refused here rather than by handing an empty pair to the client, so an unconfigured
        // connection reaches nothing that could make a request.
        if (!ConnectionTester.TryReadConnection(connection?.Address, apiKey, out var baseAddress, out _))
        {
            return new BackstopPassResult(BackstopPassOutcome.NotConfigured, null, 0, 0, 0, 0, 0);
        }

        var walk = await WalkAsync(generation, baseAddress, apiKey, connection!.BackstopWatermarkUtc, ct)
            .ConfigureAwait(false);

        // A refused pass leaves the mark where it was. Moving it forward over pages the walk declined
        // to read would skip those records for good, which is the failure the refusal exists to avoid.
        if (walk.Outcome == BackstopPassOutcome.FirstConnect)
        {
            // A mark is written even against an instance with no history: without one, every later
            // pass is another first connect.
            await MarkAsync(generation, walk.Watermark ?? clock.GetUtcNow(), ct).ConfigureAwait(false);
            await RecordPositionLostAsync(ct).ConfigureAwait(false);
        }
        else if (walk is { Outcome: BackstopPassOutcome.Walked, Watermark: { } reached })
        {
            await MarkAsync(generation, reached, ct).ConfigureAwait(false);
        }
        else if (IsRefusal(walk.Outcome))
        {
            WhisparrSyncLog.BackstopPassRefused(log, generation, walk.Outcome, baseAddress.Host);
            await RecordFailureAsync(walk.Outcome, ct).ConfigureAwait(false);
        }

        // The pass boundary is a batch boundary: whatever this walk imported is covered by one scan
        // rather than by one per record.
        followUp.Flush(library);
        return walk;
    }

    private static bool IsRefusal(BackstopPassOutcome outcome)
        => outcome is BackstopPassOutcome.RefusedPageOrder
            or BackstopPassOutcome.RefusedUnreadableAnswer
            or BackstopPassOutcome.RefusedUnreachable;

    // Counters and two instants. No page, record, file or candidate collection is held across an
    // iteration: however far the walk reads, what it hands back is the same size.
    private async Task<BackstopPassResult> WalkAsync(
        WhisparrGeneration generation,
        Uri baseAddress,
        string apiKey,
        DateTimeOffset? mark,
        CancellationToken ct)
    {
        var page = 1;
        var taken = 0;
        var imported = 0;
        var withoutCandidate = 0;
        var contained = 0;
        DateTimeOffset? newest = null;
        DateTimeOffset? previousPageOldest = null;

        while (true)
        {
            WhisparrResponse answer;
            try
            {
                answer = await client
                    .ReadHistoryAsync(baseAddress, apiKey, generation, page, PageSize, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Above the broad catch, so a shutdown classifies as cancelled rather than as a
                // failed pass.
                throw;
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
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

            var reading = WatermarkGuard.Read(instants, mark, newest, previousPageOldest);
            newest = reading.Newest;
            if (reading.Refused)
            {
                return Ended(BackstopPassOutcome.RefusedPageOrder);
            }

            for (var index = 0; index < reading.Take; index++)
            {
                taken++;
                switch (HistoryProjector.Read(generation, records[index] as JsonObject))
                {
                    case { Outcome: HistoryProjectionOutcome.Projected, Candidate: { } candidate }:
                        // Guarded per record, because the pass's mark means "history up to here has
                        // been read". One record the ingest cannot take must not decide whether the
                        // mark is written: a walk that ends in a throw leaves the mark where it was,
                        // and every later pass then reads the same page and throws on the same
                        // record for ever.
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
                            // Above the broad catch, so a shutdown classifies as cancelled rather
                            // than as a record that could not be taken.
                            throw;
                        }
#pragma warning disable CA1031 // The point of the guard is that no failure ends the walk.
                        catch (Exception failure)
                        {
                            WhisparrSyncLog.BackstopRecordContained(log, generation, failure);
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

    // Folded onto whatever the gate loads rather than onto the blob this pass opened with: the ingest
    // core writes the refusal aggregate to the same blob while the walk runs.
    private async Task MarkAsync(
        WhisparrGeneration generation, DateTimeOffset mark, CancellationToken ct)
        => await gate.MutateAsync(
            options,
            stored => stored.ConnectionFor(generation) is { } connection
                ? stored.WithConnectionFor(generation, connection with { BackstopWatermarkUtc = mark })
                : stored,
            ct).ConfigureAwait(false);

    private async Task RecordPositionLostAsync(CancellationToken ct)
        => await gate.MutateAsync(
            options,
            stored => stored with
            {
                ImportHealth = stored.ImportHealth with { BackstopPositionLost = true },
            },
            ct).ConfigureAwait(false);

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
