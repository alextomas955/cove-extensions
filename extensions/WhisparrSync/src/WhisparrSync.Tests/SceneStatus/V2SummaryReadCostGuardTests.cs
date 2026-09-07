using Probe = WhisparrSync.Tests.TestSupport.V2SiteWalkProbe;

namespace WhisparrSync.Tests.SceneStatus;

/// <summary>
/// The falsifications for the retained-heap probe's two guards: that each one fires on the shape it exists to
/// reject, with its failure message echoed so the falsification is re-observable in a run log.
/// </summary>
/// <remarks>
/// <para>
/// They live apart from the measurement because each of them builds a corpus and drops it — tens of megabytes,
/// at both site counts — and the class whose figure is a retained-heap delta must not be the class
/// manufacturing the release its own window has to reject.
/// </para>
/// <para>
/// Assembly-wide serialization does not remove that hazard. A completed neighbour's object graph is reclaimed
/// at an instant nothing here controls, so a release can still land inside a measurement window in another
/// class; that is what the window check exists for. Separating these two removes only the self-inflicted case,
/// where the class holding the measurement was also the source of the release.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class V2SummaryReadCostGuardTests
{
    [Fact]
    public async Task The_still_walking_guard_fires_when_the_sample_is_taken_after_the_walk_returns()
    {
        // The falsification: a walk that has already returned roots nothing, so a figure taken then describes
        // the collector rather than the read — and a number would still be printed.
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => Probe.MeasureAsync(Probe.SmallSites, Probe.Read.Streamed, sampleAfterTheWalk: true));

        Assert.Contains("no site was open", failure.Message, StringComparison.Ordinal);
        Console.WriteLine("GUARD FIRED: " + Flatten(failure.Message));
    }

    [Fact]
    public async Task The_pre_walk_baseline_guard_fires_when_the_harness_builds_every_site_body_up_front()
    {
        // The falsification: a transport that materialises every site's response body holds the term the
        // measurement claims was deleted, at both sizes, so the "after" figure would measure the fixture.
        var small = await Probe.MeasureAsync(Probe.SmallSites, Probe.Read.Streamed, preBuiltBodies: true);
        var large = await Probe.MeasureAsync(Probe.LargeSites, Probe.Read.Streamed, preBuiltBodies: true);

        Probe.Report("pre-built harness", small, large);

        var failure = Assert.ThrowsAny<Exception>(() => Probe.AssertBaselineDoesNotScale(small, large));

        Assert.Contains("pre-walk baseline", failure.Message, StringComparison.Ordinal);
        Console.WriteLine("GUARD FIRED: " + Flatten(failure.Message));
    }

    private static string Flatten(string message) => message.Replace("\n", " | ", StringComparison.Ordinal);
}
