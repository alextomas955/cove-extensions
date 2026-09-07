using WhisparrSync.Contracts;
using WhisparrSync.Ingest;
using WhisparrSync.State;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// Pre-reduced parity for the <c>/import-log</c> <c>syncHealth</c> banner: the reduction folds outcomes into the
/// bounded status record at WRITE time (a later success clears the window), and <see cref="Ext.SyncHealthOf"/>
/// projects that record. These drive the write path and assert the produced view matches the behavior the old
/// recomputed-from-<c>entries</c> summary gave — counts ONLY the path-mismatch failures since the last
/// successful import, so a later success clears the banner and an unrelated flag reason never trips it.
/// </summary>
[Trait("Tier", "L0")]
public sealed class SyncHealthTests
{
    private static readonly string Mismatch = IngestCoordinator.PathNotVisibleReason;

    // Drives the outcomes through the real write-time reduction, then projects the stored record — proving the
    // reduction + projection produce the same banner view the old read-time recompute did.
    private static async Task<SyncHealthView> HealthAfter(params (long Ticks, string Result, string? Reason, string Path)[] outcomes)
    {
        var log = new ImportLog(new FakeStore());
        foreach (var (ticks, result, reason, path) in outcomes)
        {
            await log.RecordOutcomeAsync(fromWebhook: true, ticks, result, reason, path, default);
        }

        return Ext.SyncHealthOf(await log.LoadStatusAsync());
    }

    [Fact]
    public async Task CountsOnlyMismatchesSinceLastSuccess_NewestFirst()
    {
        var health = await HealthAfter(
            (10, "Flagged", Mismatch, "/data/media/before.mkv"),          // before the success → cleared
            (20, "Imported", null, "/data/media/ok.mkv"),                  // the success resets the window
            (30, "Flagged", Mismatch, "/data/media/b.mkv"),
            (35, "Flagged", "ingest failed (IOException)", "/x/io.mkv"),   // wrong reason → not counted
            (40, "Flagged", Mismatch, "/data/media/c.mkv"));

        Assert.Equal(2, health.PathMismatch);
        Assert.Equal(40, health.LastMismatchTicks);
        Assert.Equal(new[] { "/data/media/c.mkv", "/data/media/b.mkv" }, health.SamplePaths);
    }

    [Fact]
    public async Task Healthy_WhenTheLatestImportSucceeded()
    {
        var health = await HealthAfter(
            (10, "Flagged", Mismatch, "/data/media/x.mkv"),
            (20, "Imported", null, "/data/media/ok.mkv"));

        Assert.Equal(0, health.PathMismatch);
        Assert.Null(health.LastMismatchTicks);
        Assert.Empty(health.SamplePaths);
    }

    [Fact]
    public async Task CountsAllMismatches_WhenThereHasNeverBeenASuccess()
    {
        var health = await HealthAfter(
            (10, "Flagged", Mismatch, "/data/media/x.mkv"),
            (20, "Flagged", Mismatch, "/data/media/y.mkv"));

        Assert.Equal(2, health.PathMismatch);
    }

    [Fact]
    public async Task AMismatchAtTheExactSuccessTick_DoesNotCount()
    {
        // A success and a mismatch can share the same 100ns tick; the strict `> lastSuccess` boundary means the
        // success wins the tie (the window is "strictly after the last success"), so nothing nags.
        var health = await HealthAfter(
            (20, "Imported", null, "/data/media/ok.mkv"),
            (20, "Flagged", Mismatch, "/data/media/x.mkv"));

        Assert.Equal(0, health.PathMismatch);
    }

    [Fact]
    public async Task SamplePaths_AreNewestFirst_Deduped_AndCappedAtThree()
    {
        var health = await HealthAfter(
            (10, "Flagged", Mismatch, "/data/media/a.mkv"),
            (20, "Flagged", Mismatch, "/data/media/b.mkv"),
            (30, "Flagged", Mismatch, "/data/media/b.mkv"), // repeat of b → deduped in the sample
            (40, "Flagged", Mismatch, "/data/media/c.mkv"),
            (50, "Flagged", Mismatch, "/data/media/d.mkv"));

        Assert.Equal(5, health.PathMismatch); // the count is every failure, not the deduped sample
        Assert.Equal(new[] { "/data/media/d.mkv", "/data/media/c.mkv", "/data/media/b.mkv" }, health.SamplePaths);
    }
}
