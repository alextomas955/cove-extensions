namespace WhisparrSync.Import;

/// <summary>Where a walk stops on one page, and where the mark moves to.</summary>
/// <param name="Refused">
/// The page could not be placed against the history the walk has already read. The walk imports
/// nothing from it.
/// </param>
/// <param name="Skip">
/// How many of the page's leading records the walk passes over: records the page before it already
/// carried, because the route shifted the page under the walk.
/// </param>
/// <param name="Take">
/// How many of the page's records the walk takes, counting from <paramref name="Skip"/>. Zero when
/// the mark is at or after the first record it would have read.
/// </param>
/// <param name="Continue">
/// Whether the page held nothing past what was skipped and taken, so an older page may hold more.
/// </param>
/// <param name="Newest">The newest instant the walk has seen, carried across pages.</param>
/// <param name="PageNewest">
/// The newest instant on the page just read, or null when it held no records. Handed back to the next
/// read, where it and the page's oldest instant together say whether a page has already been seen.
/// </param>
internal sealed record WatermarkReading(
    bool Refused, int Skip, int Take, bool Continue, DateTimeOffset? Newest, DateTimeOffset? PageNewest);

/// <summary>
/// The stop rule a backstop walk runs on: how far into a page of history it reads, and what the
/// stored mark becomes.
/// </summary>
/// <remarks>
/// A record whose instant equals the mark is taken again rather than skipped. Two records sharing one
/// instant may therefore be read twice; the dedupe downstream is by resolved path.
/// <para>
/// A page is placed against the one before it by the record ids the two share. A page repeating the
/// whole of the previous page is refused. A page opening on the records the previous page ended with
/// is one the route shifted under the walk - records arriving at the head of an offset-paged history
/// push the window back - and it is read on from the first record the walk has not seen rather than
/// refused.
/// </para>
/// <para>
/// A page carrying no id is placed by its instants instead. The order is then read from the page
/// rather than asked of it: a page whose records do not descend is refused, and so is one whose whole
/// instant range repeats the previous page's, which is the only repeated shape the across-page order
/// check admits.
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
    /// than that is refused unless the ids place it: the pages are not descending through the history.
    /// </param>
    /// <param name="previousPageNewest">
    /// The newest instant on the page before this one, or null when there was none. With no
    /// predecessor there is no range for this page to repeat, so the range rule cannot fire.
    /// </param>
    /// <param name="ids">
    /// The page's records' ids, in the order the page listed them, or null when the page carried none.
    /// </param>
    /// <param name="previousPageIds">
    /// The ids of the page before this one, or null on the first and when that page carried none.
    /// </param>
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

        // A page whose whole instant range repeats the one before it, with nothing on it older than
        // the mark, is a page this walk has already read. Refused rather than continued: the walk has
        // no way to reach older history through a route answering it, and the refusal leaves the mark
        // alone so nothing is stepped over. Only consulted where the ids did not place the page: they
        // tell this shape from a run of records genuinely sharing one instant, and the instants cannot.
        if (identity == PageIdentity.Unknown
            && take == instants.Count
            && Repeats(instants, previousPageOldest, previousPageNewest))
        {
            return new WatermarkReading(true, 0, 0, false, seen, pageNewest);
        }

        return new WatermarkReading(
            false, skip, take, skip + take == instants.Count && take > 0, seen, pageNewest);
    }

    /// <summary>How the records on a page relate to those on the page read before it.</summary>
    private enum PageIdentity
    {
        /// <summary>One of the two pages carried no ids, so only the instants place them.</summary>
        Unknown,

        /// <summary>The page holds no record the page before it held.</summary>
        Fresh,

        /// <summary>The page opens on records the page before it ended with.</summary>
        Shifted,

        /// <summary>The page opens on every record the page before it held.</summary>
        Repeated,
    }

    /// <summary>
    /// What <paramref name="ids"/> say about this page, and how many of its leading records
    /// <paramref name="previousPageIds"/> already carried.
    /// </summary>
    /// <remarks>
    /// The longest overlap wins, so a page the route shifted is read on from the first record the walk
    /// has not seen.
    /// </remarks>
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

    /// <summary>
    /// Whether <paramref name="page"/> opens on <paramref name="previous"/> read from
    /// <paramref name="start"/> onwards.
    /// </summary>
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
