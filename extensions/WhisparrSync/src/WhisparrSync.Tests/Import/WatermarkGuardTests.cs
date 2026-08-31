using WhisparrSync.Import;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// The stop rule, over pages of instants and nothing else.
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
