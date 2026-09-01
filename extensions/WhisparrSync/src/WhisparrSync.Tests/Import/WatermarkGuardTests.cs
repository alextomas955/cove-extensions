using WhisparrSync.Import;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// The stop rule, over pages of record ids and instants and nothing else.
/// </summary>
/// <remarks>
/// The two directions this rule can be wrong in are the phase's two named failure modes: stopping one
/// record early skips an import forever, and stopping one record late replays one. Both are asserted,
/// and so is the tie, which is the case the rule deliberately resolves towards replaying.
/// </remarks>
public sealed class WatermarkGuardTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithNoMarkNothingIsTakenAndTheNewestInstantIsReported()
    {
        var reading = WatermarkGuard.Read(Descending(3), null, null, null);

        Assert.False(reading.Refused);
        Assert.Equal(0, reading.Take);
        Assert.False(reading.Continue);
        Assert.Equal(Noon, reading.Newest);
    }

    [Fact]
    public void WithNoMarkAndNoRecordsTheNewestInstantIsNull()
        => Assert.Null(WatermarkGuard.Read([], null, null, null).Newest);

    /// <summary>The walk stops at the first record at or before the mark.</summary>
    [Fact]
    public void TheWalkStopsAtTheMark()
    {
        // Noon, Noon-1m, Noon-2m, Noon-3m; the mark sits on the third.
        var reading = WatermarkGuard.Read(Descending(4), Noon.AddMinutes(-2), null, null);

        Assert.Equal(3, reading.Take);
        Assert.False(reading.Continue);
    }

    /// <summary>
    /// A record whose instant equals the mark is taken again.
    /// </summary>
    /// <remarks>
    /// The tie resolves towards reading a record twice rather than towards skipping one: a repeat is
    /// a no-op under the resolved-path dedupe, and a skip is an import that never happens.
    /// </remarks>
    [Fact]
    public void ARecordAtTheMarkIsTakenRatherThanSkipped()
    {
        var atOneInstant = new[] { Noon, Noon, Noon };

        Assert.Equal(3, WatermarkGuard.Read(atOneInstant, Noon, null, null).Take);
    }

    /// <summary>Two records sharing one instant are both taken.</summary>
    [Fact]
    public void TwoRecordsSharingOneInstantAreBothTaken()
    {
        var reading = WatermarkGuard.Read(
            [Noon, Noon, Noon.AddMinutes(-5)], Noon.AddMinutes(-1), null, null);

        Assert.Equal(2, reading.Take);
    }

    /// <summary>A page taken whole asks for the next one.</summary>
    [Fact]
    public void APageTakenWholeContinues()
    {
        var reading = WatermarkGuard.Read(Descending(3), Noon.AddMinutes(-30), null, null);

        Assert.Equal(3, reading.Take);
        Assert.True(reading.Continue);
    }

    /// <summary>An empty page does not continue.</summary>
    [Fact]
    public void AnEmptyPageDoesNotContinue()
    {
        var reading = WatermarkGuard.Read([], Noon.AddMinutes(-30), Noon, null);

        Assert.Equal(0, reading.Take);
        Assert.False(reading.Continue);
        Assert.Equal(Noon, reading.Newest);
    }

    /// <summary>A page whose records ascend is refused, and nothing is taken from it.</summary>
    [Fact]
    public void AnAscendingPageIsRefused()
    {
        var reading = WatermarkGuard.Read(
            [Noon.AddMinutes(-5), Noon], Noon.AddMinutes(-30), null, null);

        Assert.True(reading.Refused);
        Assert.Equal(0, reading.Take);
        Assert.False(reading.Continue);
    }

    /// <summary>A page starting newer than the previous page's oldest record is refused.</summary>
    /// <remarks>
    /// The pages are not descending through the history, which is what a route answering every page
    /// with the same one looks like from here.
    /// </remarks>
    [Fact]
    public void APageThatDoesNotFollowTheOneBeforeItIsRefused()
    {
        var reading = WatermarkGuard.Read(
            Descending(3), Noon.AddMinutes(-30), Noon, previousPageOldest: Noon.AddMinutes(-10));

        Assert.True(reading.Refused);
        Assert.Equal(0, reading.Take);
    }

    /// <summary>A page continuing from the previous page's oldest instant is not refused.</summary>
    /// <remarks>
    /// The discriminating control for the refusal above: without it that assertion would equally pass
    /// against a rule that refused every page carrying a predecessor.
    /// </remarks>
    [Fact]
    public void APageContinuingFromTheOneBeforeItIsAccepted()
    {
        var reading = WatermarkGuard.Read(
            [Noon.AddMinutes(-10), Noon.AddMinutes(-11)],
            Noon.AddMinutes(-30),
            Noon,
            previousPageOldest: Noon.AddMinutes(-10));

        Assert.False(reading.Refused);
        Assert.Equal(2, reading.Take);
    }

    /// <summary>
    /// A page whose whole instant range repeats the previous page's, with nothing on it past the
    /// mark, is refused.
    /// </summary>
    /// <remarks>
    /// This is the shape a route ignoring its page parameter produces once every record on the page
    /// shares one instant, which is the only repeated shape the across-page order check admits.
    /// Refusing leaves the mark alone, so the history is read again rather than stepped over.
    /// <para>
    /// The page carries no id, so this is the rule reading the instants alone.
    /// </para>
    /// </remarks>
    [Fact]
    public void APageRepeatingThePreviousPagesWholeRangeIsRefused()
    {
        var atOneInstant = new[] { Noon, Noon, Noon };

        var reading = WatermarkGuard.Read(
            atOneInstant,
            Noon.AddMinutes(-30),
            Noon,
            previousPageOldest: Noon,
            previousPageNewest: Noon);

        Assert.True(reading.Refused);
        Assert.Equal(0, reading.Take);
        Assert.False(reading.Continue);
    }

    /// <summary>
    /// A page repeating the previous page's range that DOES carry a record past the mark is still
    /// walked.
    /// </summary>
    /// <remarks>
    /// The discriminating control for the refusal above: a rule keyed on the repeated range alone
    /// would swallow the records this page is the walk's only chance to read.
    /// </remarks>
    [Fact]
    public void APageRepeatingTheRangeButReachingPastTheMarkIsWalked()
    {
        var reading = WatermarkGuard.Read(
            [Noon, Noon, Noon.AddMinutes(-30)],
            Noon.AddMinutes(-10),
            Noon,
            previousPageOldest: Noon,
            previousPageNewest: Noon);

        Assert.False(reading.Refused);
        Assert.Equal(2, reading.Take);
        Assert.False(reading.Continue);
    }

    /// <summary>
    /// A page whose oldest instant repeats but whose newest does not is walked.
    /// </summary>
    /// <remarks>
    /// Reading the instants alone, the range is what says a page has been seen before, not either end
    /// of it alone. A tie run spanning a boundary reaches this rule in this shape; a run filling the
    /// whole page repeats the range too, and is told apart by its ids instead.
    /// </remarks>
    [Fact]
    public void APageSharingOnlyTheBoundaryInstantIsWalked()
    {
        var reading = WatermarkGuard.Read(
            [Noon.AddMinutes(-10), Noon.AddMinutes(-10)],
            Noon.AddMinutes(-30),
            Noon,
            previousPageOldest: Noon.AddMinutes(-10),
            previousPageNewest: Noon);

        Assert.False(reading.Refused);
        Assert.Equal(2, reading.Take);
        Assert.True(reading.Continue);
    }

    /// <summary>A page opening on the records the one before it ended with is read on from there.</summary>
    /// <remarks>
    /// Records arriving at the head of an offset-paged history push the window back, so the next page
    /// begins inside the page already read. It starts newer than that page ended, which by the
    /// instants alone is the shape of a route not paging at all - the shared ids are what tell the two
    /// apart, and what say where the walk has not read yet.
    /// </remarks>
    [Fact]
    public void APageTheRouteShiftedIsReadOnFromTheFirstUnseenRecord()
    {
        var reading = WatermarkGuard.Read(
            [Noon.AddMinutes(-1), Noon.AddMinutes(-2), Noon.AddMinutes(-3), Noon.AddMinutes(-4)],
            Noon.AddMinutes(-30),
            Noon,
            previousPageOldest: Noon.AddMinutes(-2),
            previousPageNewest: Noon,
            ids: ["29", "28", "27", "26"],
            previousPageIds: ["31", "30", "29", "28"]);

        Assert.False(reading.Refused);
        Assert.Equal(2, reading.Skip);
        Assert.Equal(2, reading.Take);
        Assert.True(reading.Continue);
    }

    /// <summary>
    /// A page sharing no record with the one before it and starting newer than it ended is still
    /// refused.
    /// </summary>
    /// <remarks>
    /// The discriminating control for the shift above: a rule that let any page carrying ids through
    /// the across-page order check would walk a route answering out of order.
    /// </remarks>
    [Fact]
    public void APageOfUnseenRecordsThatDoesNotFollowTheOneBeforeItIsRefused()
    {
        var reading = WatermarkGuard.Read(
            Descending(3),
            Noon.AddMinutes(-30),
            Noon,
            previousPageOldest: Noon.AddMinutes(-10),
            previousPageNewest: Noon.AddMinutes(-5),
            ids: ["28", "27", "26"],
            previousPageIds: ["31", "30", "29"]);

        Assert.True(reading.Refused);
        Assert.Equal(0, reading.Take);
    }

    /// <summary>A page carrying every record the one before it carried is refused.</summary>
    /// <remarks>
    /// The shape a route ignoring its page parameter produces. Its instants descend within the page
    /// and from the previous one, so the ids are the whole of what refuses it.
    /// </remarks>
    [Fact]
    public void APageCarryingEveryRecordOfTheOneBeforeItIsRefused()
    {
        string[] sameRecords = ["31", "30", "29"];

        var reading = WatermarkGuard.Read(
            [Noon, Noon, Noon],
            Noon.AddMinutes(-30),
            Noon,
            previousPageOldest: Noon,
            previousPageNewest: Noon,
            ids: sameRecords,
            previousPageIds: sameRecords);

        Assert.True(reading.Refused);
        Assert.Equal(0, reading.Take);
        Assert.False(reading.Continue);
    }

    /// <summary>
    /// A page repeating the previous page's whole instant range with records it has not seen is
    /// walked.
    /// </summary>
    /// <remarks>
    /// The discriminating control for the refusal above, and the shape a bulk import of more than one
    /// page of records at one instant produces: by the instants alone the two are the same page, and
    /// refusing this one is how the run past it is never reached.
    /// </remarks>
    [Fact]
    public void APageOfUnseenRecordsRepeatingThePreviousRangeIsWalked()
    {
        var reading = WatermarkGuard.Read(
            [Noon, Noon, Noon],
            Noon.AddMinutes(-30),
            Noon,
            previousPageOldest: Noon,
            previousPageNewest: Noon,
            ids: ["28", "27", "26"],
            previousPageIds: ["31", "30", "29"]);

        Assert.False(reading.Refused);
        Assert.Equal(0, reading.Skip);
        Assert.Equal(3, reading.Take);
        Assert.True(reading.Continue);
    }

    /// <summary>The page's own newest instant is reported for the next read to compare against.</summary>
    [Fact]
    public void ThePagesOwnNewestInstantIsReported()
    {
        var reading = WatermarkGuard.Read(
            [Noon.AddMinutes(-10), Noon.AddMinutes(-11)],
            Noon.AddMinutes(-30),
            Noon,
            previousPageOldest: Noon.AddMinutes(-5));

        Assert.Equal(Noon.AddMinutes(-10), reading.PageNewest);
        Assert.Null(WatermarkGuard.Read([], Noon, Noon, null).PageNewest);
    }

    /// <summary>The newest instant is fixed by the first page and carried unchanged.</summary>
    [Fact]
    public void TheNewestInstantIsCarriedRatherThanRecomputed()
    {
        var reading = WatermarkGuard.Read(
            [Noon.AddMinutes(-10)], Noon.AddMinutes(-30), Noon, previousPageOldest: Noon.AddMinutes(-5));

        Assert.Equal(Noon, reading.Newest);
    }

    /// <summary>Instants one minute apart, newest first.</summary>
    private static DateTimeOffset[] Descending(int count)
        => [.. Enumerable.Range(0, count).Select(index => Noon.AddMinutes(-index))];
}
