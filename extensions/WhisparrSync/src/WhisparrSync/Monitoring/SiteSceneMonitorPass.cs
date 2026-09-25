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

// Holds one chunk of numbers, asked about in one read and dropped; nothing else outlives one
// scene. How many scenes the pass resolves is unbounded on purpose, a ceiling being a total that
// reads complete while part of the library goes unmonitored. What is bounded is outstanding reads:
// each is awaited before the next, the instance's and the provider's queues being the shared
// resource. Nothing stops the pass, so a site that fails leaves the next one offered.
internal static class SiteSceneMonitorPass
{
    // A bound on what is held at once, not a ceiling on how many scenes the pass reaches; a larger
    // site is read in as many chunks as it takes. The instance narrows its own answer by no
    // parameter, so it answers the site's whole list however few numbers were asked about: a
    // smaller chunk costs more of those whole-list reads.
    internal const int ChunkSize = 500;

    // Null where the provider stopped answering, which the pass counts as unnumbered rather than
    // propagating: a provider that stopped leaves the rest of the library to offer.
    private static async Task<int?> NumberForAsync(
        SiteSceneMonitorPorts ports,
        string identity,
        ILogger log,
        ReportedOnce reported,
        CancellationToken ct)
    {
        try
        {
            return await ports.NumberFor(identity, ct).ConfigureAwait(false);
        }
        // A stop arrives in one of the shapes the containment below names. Contained, it would end
        // the run Completed on a tally that reads like a finished pass.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            if (reported.NotYet())
            {
                WhisparrSyncLog.SceneNumbersUnreadable(log, WhisparrSyncLog.Classify(failure));
            }

            return null;
        }
    }

    // One line per site: a line per scene would fill the log with the same fact repeated, and the
    // tally already carries how many.
    private sealed class ReportedOnce
    {
        private bool _fired;

        internal bool NotYet()
        {
            if (_fired)
            {
                return false;
            }

            _fired = true;
            return true;
        }
    }

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
        var numbersReported = new ReportedOnce();
        var rowsReported = new ReportedOnce();

        await foreach (var identity in ports.OwnedScenes(site.StudioId, ct)
            .WithCancellation(ct)
            .ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var number = await NumberForAsync(ports, identity, log, numbersReported, ct)
                .ConfigureAwait(false);

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
            var rows = await RowsForAsync(ports, siteId, chunk, log, rowsReported, ct)
                .ConfigureAwait(false);

            // The read raises rather than answering an empty map, so these scenes are counted as
            // unresolved rather than reported as scenes the instance holds nothing for.
            if (rows is null)
            {
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

    // Null where the instance stopped answering, which the caller counts as unresolved.
    private static async Task<IReadOnlyDictionary<int, int>?> RowsForAsync(
        SiteSceneMonitorPorts ports,
        int siteId,
        IReadOnlyList<int> chunk,
        ILogger log,
        ReportedOnce reported,
        CancellationToken ct)
    {
        try
        {
            return await ports.RowsFor(siteId, chunk, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            if (reported.NotYet())
            {
                WhisparrSyncLog.SiteSceneRowsUnreadable(log, WhisparrSyncLog.Classify(failure));
            }

            return null;
        }
    }
}
