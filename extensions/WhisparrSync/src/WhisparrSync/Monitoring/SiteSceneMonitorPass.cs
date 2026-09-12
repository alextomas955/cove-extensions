using Microsoft.Extensions.Logging;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

/// <summary>What one site's own scene pass reaches the outside world through.</summary>
/// <remarks>
/// Delegates rather than the seams themselves, so the pass performs no I/O of its own and each step
/// is assertable without a mounted host. Which generation and which instance each delegate acts
/// against is bound where the run is composed; nothing here reads a generation.
/// </remarks>
/// <param name="OwnedScenes">
/// The identifiers the scenes the reader owns on one studio carry, by Cove's own id for that studio.
/// Streamed, because a studio's own catalogue is as large as the reader's files under it.
/// </param>
/// <param name="NumberFor">
/// The number the connected generation names one stored scene identifier by, or null where the
/// metadata provider answers none for it.
/// </param>
/// <param name="RowsFor">
/// Which of a set of those numbers one site holds a row for, and the row's own identifier.
/// </param>
/// <param name="SetMonitored">
/// Marks the row one identifier names wanted, answering whatever the instance said or nothing where
/// the request reached it.
/// </param>
internal sealed record SiteSceneMonitorPorts(
    Func<int, CancellationToken, IAsyncEnumerable<string>> OwnedScenes,
    Func<string, CancellationToken, Task<int?>> NumberFor,
    Func<int, IReadOnlyCollection<int>, CancellationToken, Task<IReadOnlyDictionary<int, int>>> RowsFor,
    Func<int, CancellationToken, Task<WhisparrResponse?>> SetMonitored);

/// <summary>
/// Marks every scene the reader owns on one registered site wanted, on the generation whose scenes
/// live under a site.
/// </summary>
/// <remarks>
/// The one part of this run that holds a collection: a chunk of numbers, bounded by
/// <see cref="ChunkSize"/>, asked about in one read and dropped. Nothing else here outlives one
/// scene, and nothing grows with the library.
/// <para>
/// Nothing bounds how many scenes the pass resolves. Every scene the reader owns on the site is read,
/// however many there are. A ceiling would leave part of the library unmonitored and report a total
/// that reads exactly like a complete one. What is bounded is how many reads are outstanding at a
/// time, which is <see cref="SyncPreviewJob.SitePresenceReadsInFlight"/> and is why every read here
/// is awaited before the next is issued - the instance's and the provider's request queues are the
/// shared resource, and that is the same bound for the same reason.
/// </para>
/// <para>
/// Every step counts and nothing stops the pass. A scene the provider names no number for, a scene
/// the instance holds no row for and a flag the instance declines are each counted and the next scene
/// is read; a read that could not be answered at all counts the scenes it was asked about and the
/// next chunk is still read, so a site that fails leaves the run offering the next site.
/// </para>
/// </remarks>
internal static class SiteSceneMonitorPass
{
    /// <summary>How many scene numbers one row read is asked about.</summary>
    /// <remarks>
    /// A bound on what is held at once, and on nothing else. The instance narrows its own answer by
    /// no parameter, so it answers the site's whole list however few numbers were asked about: a
    /// smaller chunk costs more of those whole-list reads and a larger one holds more numbers in
    /// memory at a time. 500 reads a site the size of the one measured on 2026-09-10 - 412 scenes -
    /// in a single read, while holding half a kilobyte of integers.
    /// <para>
    /// It is not a ceiling on how many scenes the pass reaches. A site carrying more scenes than this
    /// is read in as many chunks as it takes.
    /// </para>
    /// </remarks>
    internal const int ChunkSize = 500;

    /// <summary>
    /// Marks the scenes the reader owns on <paramref name="site"/> wanted, on the instance's own site
    /// <paramref name="siteId"/> names.
    /// </summary>
    /// <param name="ports">What the pass reaches the provider and the instance through.</param>
    /// <param name="site">The site, carrying Cove's own studio id beside the identifier.</param>
    /// <param name="siteId">The instance's own id for that site, off the answer the run already read.</param>
    /// <param name="log">This extension's own logger.</param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
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
            catch (Exception failure) when (failure is HttpRequestException or IOException)
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
            catch (Exception failure) when (failure is HttpRequestException or IOException)
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
