using Microsoft.Extensions.Logging;
using WhisparrSync.Library;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

// Which generation and which instance each delegate acts against is bound where the run is
// composed; nothing here reads a generation. OwnedScenes streams, because a studio's catalogue is
// as large as the reader's files under it.
internal sealed record SiteSceneMonitorPorts(
    Func<int, CancellationToken, IAsyncEnumerable<string>> OwnedScenes,
    Func<string, CancellationToken, Task<int?>> NumberFor,
    Func<int, IReadOnlyCollection<int>, CancellationToken, Task<IReadOnlyDictionary<int, int>>> RowsFor,
    Func<int, CancellationToken, Task<WhisparrResponse?>> SetMonitored);

// The one part of this run that holds a collection: one chunk of numbers, asked about in one read
// and dropped. Nothing else outlives one scene.
//
// Nothing bounds how many scenes the pass resolves. A ceiling would leave part of the library
// unmonitored and report a total that reads like a complete one. What is bounded is how many reads
// are outstanding, which is SyncPreviewJob.SiteSceneReadsInFlight and is why every read here is
// awaited before the next is issued: the instance's and the provider's request queues are the
// shared resource.
//
// Every step counts and nothing stops the pass, so a site that fails leaves the run offering the
// next site.
internal static class SiteSceneMonitorPass
{
    // A bound on what is held at once, not a ceiling on how many scenes the pass reaches; a larger
    // site is read in as many chunks as it takes. The instance narrows its own answer by no
    // parameter, so it answers the site's whole list however few numbers were asked about: a
    // smaller chunk costs more of those whole-list reads.
    internal const int ChunkSize = 500;

    internal static async Task<SceneMonitorTally> MonitorAsync(
        SiteSceneMonitorPorts ports,
        LibrarySiteIdentity site,
        int siteId,
        ILogger log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(site);

        var tally = SceneMonitorTally.Nothing;
        var chunk = new List<int>(ChunkSize);
        var numbersReported = false;
        var rowsReported = false;

        await foreach (var identity in ports.OwnedScenes(site.StudioId, ct)
            .WithCancellation(ct)
            .ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            int? number;
            try
            {
                number = await ports.NumberFor(identity, ct).ConfigureAwait(false);
            }
            // A stop arrives in one of the shapes the containment below names. Contained, it would
            // end the run Completed on a tally that reads like a finished pass.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
                when (failure is HttpRequestException or IOException or TaskCanceledException)
            {
                // Contained rather than propagated, because a provider that stopped answering leaves
                // the rest of the library to offer. Reported once per site: one line per scene would
                // fill the log with the same fact repeated, and the tally already carries how many.
                if (!numbersReported)
                {
                    numbersReported = true;
                    WhisparrSyncLog.SceneNumbersUnreadable(log, WhisparrSyncLog.Classify(failure));
                }

                number = null;
            }

            if (number is not { } named)
            {
                tally = tally with { Unnumbered = tally.Unnumbered + 1 };
                continue;
            }

            chunk.Add(named);
            if (chunk.Count < ChunkSize)
            {
                continue;
            }

            tally = tally.Plus(await FlagAsync().ConfigureAwait(false));
            chunk.Clear();
        }

        if (chunk.Count > 0)
        {
            tally = tally.Plus(await FlagAsync().ConfigureAwait(false));
        }

        return tally;

        async Task<SceneMonitorTally> FlagAsync()
        {
            IReadOnlyDictionary<int, int> rows;
            try
            {
                rows = await ports.RowsFor(siteId, chunk, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
                when (failure is HttpRequestException or IOException or TaskCanceledException)
            {
                // The read raises rather than answering an empty map, so these scenes are counted as
                // unresolved rather than reported as scenes the instance holds nothing for.
                if (!rowsReported)
                {
                    rowsReported = true;
                    WhisparrSyncLog.SiteSceneRowsUnreadable(log, WhisparrSyncLog.Classify(failure));
                }

                return new SceneMonitorTally(0, 0, chunk.Count, 0);
            }

            var flagged = SceneMonitorTally.Nothing;
            foreach (var number in chunk)
            {
                ct.ThrowIfCancellationRequested();

                if (!rows.TryGetValue(number, out var row))
                {
                    flagged = flagged with { Unresolved = flagged.Unresolved + 1 };
                    continue;
                }

                flagged = flagged.Plus(
                    SceneMonitorTally.For(await ports.SetMonitored(row, ct).ConfigureAwait(false)));
            }

            return flagged;
        }
    }
}
