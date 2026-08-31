namespace WhisparrSync.Import;

/// <summary>Where a walk stops on one page, and where the mark moves to.</summary>
/// <param name="Refused">
/// The page's records were not in non-increasing instant order. The walk imports nothing from it.
/// </param>
/// <param name="Take">
/// How many of the page's records the walk takes, counting from its start. Zero when the mark is at
/// or after the page's first record.
/// </param>
/// <param name="Continue">Whether every record on the page was taken, so an older page may hold more.</param>
/// <param name="Newest">The newest instant the walk has seen, carried across pages.</param>
internal sealed record WatermarkReading(
    bool Refused, int Take, bool Continue, DateTimeOffset? Newest);

/// <summary>
/// The stop rule a backstop walk runs on: how far into a page of history it reads, and what the
/// stored mark becomes.
/// </summary>
/// <remarks>
/// A record whose instant equals the mark is taken again rather than skipped. Two records sharing one
/// instant may therefore be read twice; the dedupe downstream is by resolved path.
/// <para>
/// The order is read from the page rather than asked of it. A page whose records do not descend is
/// refused, which is also what stops a walk against a route that answers every page with the same
/// one.
/// </para>
/// </remarks>
internal static class WatermarkGuard
{
    /// <summary>What one page's <paramref name="instants"/> mean for a walk at <paramref name="mark"/>.</summary>
    /// <param name="instants">The page's records' instants, in the order the page listed them.</param>
    /// <param name="mark">Where the last pass left off, or null when no pass has run.</param>
    /// <param name="newest">The newest instant seen on an earlier page, or null on the first.</param>
    /// <param name="previousPageOldest">
    /// The oldest instant on the page before this one, or null on the first. A page starting newer
    /// than that is refused: the pages are not descending through the history.
    /// </param>
    internal static WatermarkReading Read(
        IReadOnlyList<DateTimeOffset> instants,
        DateTimeOffset? mark,
        DateTimeOffset? newest,
        DateTimeOffset? previousPageOldest)
    {
        ArgumentNullException.ThrowIfNull(instants);

        if (!DescendsWithin(instants) || !DescendsFrom(previousPageOldest, instants))
        {
            return new WatermarkReading(true, 0, false, newest);
        }

        var seen = newest ?? (instants.Count > 0 ? instants[0] : null);
        if (mark is not { } stop)
        {
            // No mark to walk back to: the pass records where the history currently ends and takes
            // nothing.
            return new WatermarkReading(false, 0, false, seen);
        }

        var take = 0;
        while (take < instants.Count && instants[take] >= stop)
        {
            take++;
        }

        return new WatermarkReading(false, take, take == instants.Count && take > 0, seen);
    }

    private static bool DescendsWithin(IReadOnlyList<DateTimeOffset> instants)
    {
        for (var index = 1; index < instants.Count; index++)
        {
            if (instants[index] > instants[index - 1])
            {
                return false;
            }
        }

        return true;
    }

    private static bool DescendsFrom(
        DateTimeOffset? previousPageOldest, IReadOnlyList<DateTimeOffset> instants)
        => previousPageOldest is not { } boundary
            || instants.Count == 0
            || instants[0] <= boundary;
}
