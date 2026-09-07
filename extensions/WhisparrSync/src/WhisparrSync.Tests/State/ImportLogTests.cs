using WhisparrSync.Ingest;
using WhisparrSync.State;

namespace WhisparrSync.Tests.State;

/// <summary>
/// The write-half contract for <see cref="ImportLog"/> as a bounded status record: a webhook
/// delivery stamps the last-event tick (the poll does not), a success from either channel clears the
/// unresolved-failure window, a path-mismatch failure accrues onto the capped sample ring, the ring is
/// bounded, and a corrupt OR the pre-reduction <c>ImportLogEntry[]</c> array blob loads as the empty status
/// (never throws).
/// </summary>
[Trait("Tier", "L0")]
public sealed class ImportLogTests
{
    private static readonly string Mismatch = IngestCoordinator.PathNotVisibleReason;

    [Fact]
    public async Task WebhookEvent_StampsLastEventTick_PollDoesNot()
    {
        var log = new ImportLog(new FakeStore());

        await log.RecordOutcomeAsync(fromWebhook: true, 100L, "Skipped", "duplicate delivery", "/x.mkv");
        Assert.Equal(100L, (await log.LoadStatusAsync()).LastWebhookEventTicks);

        // A poll outcome never advances the "last event" line (a webhook-only signal).
        await log.RecordOutcomeAsync(fromWebhook: false, 200L, "Imported", null, "/y.mkv");
        Assert.Equal(100L, (await log.LoadStatusAsync()).LastWebhookEventTicks);
    }

    [Fact]
    public async Task LastEventTick_TakesTheNewest_AcrossWebhookDeliveries()
    {
        var log = new ImportLog(new FakeStore());

        await log.RecordOutcomeAsync(fromWebhook: true, 300L, "Imported", null, "/a.mkv");
        await log.RecordOutcomeAsync(fromWebhook: true, 100L, "Skipped", "duplicate delivery", "/a.mkv");

        Assert.Equal(300L, (await log.LoadStatusAsync()).LastWebhookEventTicks); // an out-of-order older event never regresses it
    }

    [Fact]
    public async Task Success_SetsLastSuccessTick_AndClearsTheUnresolvedWindow()
    {
        var log = new ImportLog(new FakeStore());
        await log.RecordOutcomeAsync(fromWebhook: true, 10L, "Flagged", Mismatch, "/data/a.mkv");
        await log.RecordOutcomeAsync(fromWebhook: true, 20L, "Flagged", Mismatch, "/data/b.mkv");

        await log.RecordOutcomeAsync(fromWebhook: true, 30L, "Imported", null, "/data/a.mkv");

        var status = await log.LoadStatusAsync();
        Assert.Equal(30L, status.LastSuccessTicks);
        Assert.Equal(0, status.UnresolvedPathMismatch); // a later success clears the banner
        Assert.Empty(status.RecentFailures);
    }

    [Fact]
    public async Task PathMismatch_AfterSuccess_AccruesTheExactCount_AndPushesTheRing()
    {
        var log = new ImportLog(new FakeStore());
        await log.RecordOutcomeAsync(fromWebhook: true, 10L, "Imported", null, "/data/a.mkv");

        await log.RecordOutcomeAsync(fromWebhook: false, 20L, "Flagged", Mismatch, "/data/b.mkv");
        await log.RecordOutcomeAsync(fromWebhook: true, 30L, "Flagged", Mismatch, "/data/c.mkv");

        var status = await log.LoadStatusAsync();
        Assert.Equal(2, status.UnresolvedPathMismatch);
        Assert.Equal(new[] { "/data/b.mkv", "/data/c.mkv" }, status.RecentFailures.Select(f => f.Path));
    }

    [Fact]
    public async Task PathMismatch_AtOrBeforeLastSuccess_DoesNotCount()
    {
        // The strict `> lastSuccess` boundary: a success ties or supersedes a same-tick mismatch, so nothing nags.
        var log = new ImportLog(new FakeStore());
        await log.RecordOutcomeAsync(fromWebhook: true, 20L, "Imported", null, "/data/a.mkv");

        await log.RecordOutcomeAsync(fromWebhook: true, 20L, "Flagged", Mismatch, "/data/b.mkv");

        Assert.Equal(0, (await log.LoadStatusAsync()).UnresolvedPathMismatch);
    }

    [Fact]
    public async Task NonMismatchFlag_StampsTheEventTick_ButNeverTripsTheBanner()
    {
        // An out-of-root reject (or any non-path-mismatch flag) is a webhook event but not a sync-broken signal.
        var log = new ImportLog(new FakeStore());

        await log.RecordOutcomeAsync(fromWebhook: true, 50L, "Flagged", "path outside known Whisparr root", "/evil.mkv");

        var status = await log.LoadStatusAsync();
        Assert.Equal(50L, status.LastWebhookEventTicks);
        Assert.Equal(0, status.UnresolvedPathMismatch);
        Assert.Empty(status.RecentFailures);
    }

    [Fact]
    public async Task RecentFailuresRing_IsCappedToTheNewest_WhileTheCountStaysExact()
    {
        var log = new ImportLog(new FakeStore());
        var cap = ImportStatus.MaxRecentFailures;

        for (var i = 1; i <= cap + 4; i++)
        {
            await log.RecordOutcomeAsync(fromWebhook: false, i, "Flagged", Mismatch, $"/data/{i}.mkv");
        }

        var status = await log.LoadStatusAsync();
        Assert.Equal(cap + 4, status.UnresolvedPathMismatch);       // the rendered count is exact, never truncated
        Assert.Equal(cap, status.RecentFailures.Length);            // the sample ring is bounded
        Assert.Equal($"/data/{cap + 4}.mkv", status.RecentFailures[^1].Path); // the newest sample survives
        Assert.Equal("/data/5.mkv", status.RecentFailures[0].Path);           // the oldest kept sample (1..4 evicted)
    }

    [Fact]
    public async Task CorruptBlob_LoadsEmpty_NeverThrows()
    {
        var store = new FakeStore();
        await store.SetAsync("importlog", "}{ not json");

        var log = new ImportLog(store);

        Assert.Equal(ImportStatus.Empty, await log.LoadStatusAsync());
        // A subsequent record still succeeds (the corrupt blob is treated as empty, then replaced).
        await log.RecordOutcomeAsync(fromWebhook: true, 99L, "Imported", null, "/a.mkv");
        Assert.Equal(99L, (await log.LoadStatusAsync()).LastSuccessTicks);
    }

    [Fact]
    public async Task OldArrayShapedBlob_DegradesToEmptyStatus_ThenReseedsOnNextWebhook()
    {
        // The pre-reduction blob is a JSON ImportLogEntry[] ARRAY; it cannot deserialize into the status
        // OBJECT, so the defensive parse loads it as the empty status (never throws). Only never-rendered
        // history is lost; the next webhook reseeds the record.
        var store = new FakeStore();
        await store.SetAsync(
            "importlog",
            "[{\"UtcTicks\":638000000000000000,\"Source\":\"webhook\",\"Result\":\"Imported\",\"Path\":\"/old.mkv\"}]");

        var log = new ImportLog(store);
        Assert.Equal(ImportStatus.Empty, await log.LoadStatusAsync());

        await log.RecordOutcomeAsync(fromWebhook: true, 500L, "Imported", null, "/new.mkv");
        var status = await log.LoadStatusAsync();
        Assert.Equal(500L, status.LastWebhookEventTicks);
        Assert.Equal(500L, status.LastSuccessTicks);
    }
}
