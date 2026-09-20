using WhisparrSync.Import;

namespace WhisparrSync.Tests.Import;

// Both directions this rule can be wrong in are asserted: stopping one record early skips an import
// for ever, and stopping one record late replays one. So is the tie, which the rule deliberately
// resolves towards replaying.
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

    [Fact]
    public void TheWalkStopsAtTheMark()
    {
        // Noon, Noon-1m, Noon-2m, Noon-3m; the mark sits on the third.
        var reading = WatermarkGuard.Read(Descending(4), Noon.AddMinutes(-2), null, null);

        Assert.Equal(3, reading.Take);
        Assert.False(reading.Continue);
    }

    // The tie resolves towards reading a record twice rather than towards skipping one: a repeat is a
    // no-op under the resolved-path dedupe, and a skip is an import that never happens.
    [Fact]
    public void ARecordAtTheMarkIsTakenRatherThanSkipped()
    {
        var atOneInstant = new[] { Noon, Noon, Noon };

        Assert.Equal(3, WatermarkGuard.Read(atOneInstant, Noon, null, null).Take);
    }

    [Fact]
    public void TwoRecordsSharingOneInstantAreBothTaken()
    {
        var reading = WatermarkGuard.Read(
            [Noon, Noon, Noon.AddMinutes(-5)], Noon.AddMinutes(-1), null, null);

        Assert.Equal(2, reading.Take);
    }

    [Fact]
    public void APageTakenWholeContinues()
    {
        var reading = WatermarkGuard.Read(Descending(3), Noon.AddMinutes(-30), null, null);

        Assert.Equal(3, reading.Take);
        Assert.True(reading.Continue);
    }

    [Fact]
    public void AnEmptyPageDoesNotContinue()
    {
        var reading = WatermarkGuard.Read([], Noon.AddMinutes(-30), Noon, null);

        Assert.Equal(0, reading.Take);
        Assert.False(reading.Continue);
        Assert.Equal(Noon, reading.Newest);
    }

    [Fact]
    public void AnAscendingPageIsRefused()
    {
        var reading = WatermarkGuard.Read(
            [Noon.AddMinutes(-5), Noon], Noon.AddMinutes(-30), null, null);

        Assert.True(reading.Refused);
        Assert.Equal(0, reading.Take);
        Assert.False(reading.Continue);
    }

    // The pages are not descending through the history, which is what a route answering every page with
    // the same one looks like from here.
    [Fact]
    public void APageThatDoesNotFollowTheOneBeforeItIsRefused()
    {
        var reading = WatermarkGuard.Read(
            Descending(3), Noon.AddMinutes(-30), Noon, previousPageOldest: Noon.AddMinutes(-10));

        Assert.True(reading.Refused);
        Assert.Equal(0, reading.Take);
    }

    // The control for the refusal above: without it that assertion would equally pass against a rule
    // that refused every page carrying a predecessor.
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

    // This is the shape a route ignoring its page parameter produces once every record on the page
    // shares one instant, which is the only repeated shape the across-page order check admits. Refusing
    // leaves the mark alone, so the history is read again rather than stepped over.
    // The page carries no id, so this is the rule reading the instants alone.
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

    // The control for the refusal above: a rule keyed on the repeated range alone would swallow the
    // records this page is the walk's only chance to read.
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

    // Reading the instants alone, the range is what says a page has been seen before, not either end of
    // it alone. A tie run spanning a boundary reaches this rule in this shape; a run filling the whole
    // page repeats the range too, and is told apart by its ids instead.
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

    // Records arriving at the head of an offset-paged history push the window back, so the next page
    // begins inside the page already read. It starts newer than that page ended, which by the instants
    // alone is the shape of a route not paging at all. The shared ids are what tell the two apart, and
    // what say where the walk has not read yet.
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

    // The control for the shift above: a rule that let any page carrying ids through the across-page
    // order check would walk a route answering out of order.
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

    // The shape a route ignoring its page parameter produces. Its instants descend within the page and
    // from the previous one, so the ids are the whole of what refuses it.
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

    // The control for the refusal above, and the shape a bulk import of more than one page of records
    // at one instant produces: by the instants alone the two are the same page, and refusing this one
    // is how the run past it is never reached.
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

    [Fact]
    public void TheNewestInstantIsCarriedRatherThanRecomputed()
    {
        var reading = WatermarkGuard.Read(
            [Noon.AddMinutes(-10)], Noon.AddMinutes(-30), Noon, previousPageOldest: Noon.AddMinutes(-5));

        Assert.Equal(Noon, reading.Newest);
    }

    private static DateTimeOffset[] Descending(int count)
        => [.. Enumerable.Range(0, count).Select(index => Noon.AddMinutes(-index))];
}
