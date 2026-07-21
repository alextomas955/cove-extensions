using WhisparrSync.Ingest;
using WhisparrSync.State;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The <c>/import-log</c> <c>syncHealth</c> summary (<see cref="Ext.SyncHealthOf"/>): counts ONLY the
/// path-mismatch import failures (Cove couldn't open Whisparr's path) since the last SUCCESSFUL import, so a
/// later success clears the settings banner and an unrelated flag reason never trips it.
/// </summary>
public sealed class SyncHealthTests
{
    private static readonly string Mismatch = IngestCoordinator.PathNotVisibleReason;

    private static ImportLogEntry Entry(long ticks, string result, string? reason, string path = "/data/media/x.mkv")
        => new(ticks, "webhook", "Download", path, "Video", null, result, reason, "k" + ticks);

    [Fact]
    public void CountsOnlyMismatchesSinceLastSuccess_NewestFirst()
    {
        ImportLogEntry[] entries =
        [
            Entry(10, "Flagged", Mismatch, "/data/media/before.mkv"),          // before the success → ignored
            Entry(20, "Imported", null),                                        // the success resets the window
            Entry(35, "Flagged", "ingest failed (IOException)", "/x/io.mkv"),   // wrong reason → not counted
            Entry(30, "Flagged", Mismatch, "/data/media/b.mkv"),
            Entry(40, "Flagged", Mismatch, "/data/media/c.mkv"),
        ];

        var health = Ext.SyncHealthOf(entries);

        Assert.Equal(2, health.PathMismatch);
        Assert.Equal(40, health.LastMismatchTicks);
        Assert.Equal(new[] { "/data/media/c.mkv", "/data/media/b.mkv" }, health.SamplePaths);
    }

    [Fact]
    public void Healthy_WhenTheLatestImportSucceeded()
    {
        ImportLogEntry[] entries = [Entry(10, "Flagged", Mismatch), Entry(20, "Imported", null)];

        var health = Ext.SyncHealthOf(entries);

        Assert.Equal(0, health.PathMismatch);
        Assert.Null(health.LastMismatchTicks);
        Assert.Empty(health.SamplePaths);
    }

    [Fact]
    public void CountsAllMismatches_WhenThereHasNeverBeenASuccess()
    {
        ImportLogEntry[] entries = [Entry(10, "Flagged", Mismatch), Entry(20, "Flagged", Mismatch)];

        Assert.Equal(2, Ext.SyncHealthOf(entries).PathMismatch);
    }

    [Fact]
    public void AMismatchAtTheExactSuccessTick_DoesNotCount()
    {
        // A success and a mismatch can share the same 100ns tick; the strict `> lastSuccess` boundary means the
        // success wins the tie (the window is "strictly after the last success"), so nothing nags.
        ImportLogEntry[] entries = [Entry(20, "Imported", null), Entry(20, "Flagged", Mismatch)];

        Assert.Equal(0, Ext.SyncHealthOf(entries).PathMismatch);
    }

    [Fact]
    public void SamplePaths_AreNewestFirst_Deduped_AndCappedAtThree()
    {
        ImportLogEntry[] entries =
        [
            Entry(10, "Flagged", Mismatch, "/data/media/a.mkv"),
            Entry(20, "Flagged", Mismatch, "/data/media/b.mkv"),
            Entry(30, "Flagged", Mismatch, "/data/media/b.mkv"), // repeat of b → deduped
            Entry(40, "Flagged", Mismatch, "/data/media/c.mkv"),
            Entry(50, "Flagged", Mismatch, "/data/media/d.mkv"),
        ];

        var health = Ext.SyncHealthOf(entries);

        Assert.Equal(5, health.PathMismatch); // count is every failure, not the deduped sample
        Assert.Equal(new[] { "/data/media/d.mkv", "/data/media/c.mkv", "/data/media/b.mkv" }, health.SamplePaths);
    }
}
