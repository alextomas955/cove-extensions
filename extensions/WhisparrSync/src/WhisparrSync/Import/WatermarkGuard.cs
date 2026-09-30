namespace WhisparrSync.Import;

// Where a walk stops on one page, and where the mark moves to. Refused means the page could not be
// placed against the history already read, so the walk imports nothing from it. Skip counts
// leading records the previous page already carried, and Take counts from there. Continue means
// the page held nothing past what was skipped and taken, so an older page may hold more.
// PageNewest is handed back to the next read, where it and the page's oldest instant together say
// whether a page has already been seen.
internal sealed record WatermarkReading(
    bool Refused, int Skip, int Take, bool Continue, DateTimeOffset? Newest, DateTimeOffset? PageNewest);

// The stop rule a backstop walk runs on: how far into a page it reads, and what the stored mark
// becomes. A record whose instant equals the mark is taken again, so two sharing one instant may be
// read twice; the dedupe downstream is by resolved path.
//
// A page is placed against the one before it by the ids they share. One repeating the whole
// previous page is refused. One opening on the records the previous page ended with is a window the
// route shifted under the walk, records arriving at the head of an offset-paged history pushing it
// back, and is read on from the first record not yet seen. A page carrying no id is placed by its
// instants: refused if its records do not descend, or if its whole range repeats the previous
// page's.
internal static class WatermarkGuard
{
    // A page starting newer than the previous page's oldest instant is refused unless the ids place
    // it, because the pages are then not descending through the history. With no predecessor there
    // is no range to repeat, so the range rule cannot fire.
    internal static WatermarkReading Read(
        IReadOnlyList<DateTimeOffset> instants,
        DateTimeOffset? mark,
        DateTimeOffset? newest,
        DateTimeOffset? previousPageOldest,
        DateTimeOffset? previousPageNewest = null,
        IReadOnlyList<string>? ids = null,
        IReadOnlyList<string>? previousPageIds = null)
    {
        ArgumentNullException.ThrowIfNull(instants);

        var pageNewest = instants.Count > 0 ? instants[0] : (DateTimeOffset?)null;
        var (identity, skip) = IdentityOf(ids, previousPageIds);

        if (!DescendsWithin(instants)
            || identity == PageIdentity.Repeated
            || (identity != PageIdentity.Shifted && !DescendsFrom(previousPageOldest, instants)))
        {
            return new WatermarkReading(true, 0, 0, false, newest, pageNewest);
        }

        var seen = newest ?? pageNewest;
        if (mark is not { } stop)
        {
            // No mark to walk back to: the pass records where the history currently ends and takes
            // nothing.
            return new WatermarkReading(false, 0, 0, false, seen, pageNewest);
        }

        var take = 0;
        while (skip + take < instants.Count && instants[skip + take] >= stop)
        {
            take++;
        }

        // A page whose whole instant range repeats the one before it, with nothing older than the
        // mark, has already been read. Refused rather than continued: the walk cannot reach older
        // history through a route answering it, and the refusal leaves the mark alone. Consulted
        // only where the ids did not place the page, because the instants alone cannot tell this
        // from a run of records sharing one instant.
        if (identity == PageIdentity.Unknown
            && take == instants.Count
            && Repeats(instants, previousPageOldest, previousPageNewest))
        {
            return new WatermarkReading(true, 0, 0, false, seen, pageNewest);
        }

        return new WatermarkReading(
            false, skip, take, skip + take == instants.Count && take > 0, seen, pageNewest);
    }

    // How the records on a page relate to those on the page read before it.
    private enum PageIdentity
    {
        // One of the two pages carried no ids, so only the instants place them.
        Unknown,

        // The page holds no record the page before it held.
        Fresh,

        // The page opens on records the page before it ended with.
        Shifted,

        // The page opens on every record the page before it held.
        Repeated,
    }

    // The longest overlap wins, so a page the route shifted is read on from the first record the
    // walk has not seen.
    private static (PageIdentity Identity, int Skip) IdentityOf(
        IReadOnlyList<string>? ids, IReadOnlyList<string>? previousPageIds)
    {
        if (ids is not { Count: > 0 } page || previousPageIds is not { Count: > 0 } previous)
        {
            return (PageIdentity.Unknown, 0);
        }

        for (var start = 0; start < previous.Count; start++)
        {
            if (!OpensWith(page, previous, start))
            {
                continue;
            }

            return start == 0
                ? (PageIdentity.Repeated, 0)
                : (PageIdentity.Shifted, previous.Count - start);
        }

        return (PageIdentity.Fresh, 0);
    }

    private static bool OpensWith(
        IReadOnlyList<string> page, IReadOnlyList<string> previous, int start)
    {
        var length = previous.Count - start;
        if (length > page.Count)
        {
            return false;
        }

        for (var index = 0; index < length; index++)
        {
            if (!string.Equals(page[index], previous[start + index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Repeats(
        IReadOnlyList<DateTimeOffset> instants,
        DateTimeOffset? previousPageOldest,
        DateTimeOffset? previousPageNewest)
        => instants.Count > 0
            && previousPageNewest == instants[0]
            && previousPageOldest == instants[^1];

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
