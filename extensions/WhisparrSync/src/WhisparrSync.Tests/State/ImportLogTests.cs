using WhisparrSync.State;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.State;

/// <summary>
/// The write-half contract for <see cref="ImportLog"/> over a fake store: an append round-trips every field,
/// successive appends accumulate one entry each, and a corrupt/hand-edited blob loads as empty (never throws).
/// </summary>
public sealed class ImportLogTests
{
    private static ImportLogEntry Entry(string result, string? reason = null)
        => new(
            UtcTicks: 638_000_000_000_000_000L,
            Source: "webhook",
            EventType: "Download",
            Path: "/data/media/Scene/Scene.mkv",
            Kind: "Video",
            CoveEntityId: 7,
            Result: result,
            Reason: reason,
            LedgerKey: "ledger-key-1");

    [Fact]
    public async Task Append_RoundTripsEveryField()
    {
        var log = new ImportLog(new FakeStore());

        await log.AppendAsync(Entry("Imported"));
        var entry = Assert.Single(await log.LoadAllAsync());

        Assert.Equal(638_000_000_000_000_000L, entry.UtcTicks);
        Assert.Equal("webhook", entry.Source);
        Assert.Equal("Download", entry.EventType);
        Assert.Equal("/data/media/Scene/Scene.mkv", entry.Path);
        Assert.Equal("Video", entry.Kind);
        Assert.Equal(7, entry.CoveEntityId);
        Assert.Equal("Imported", entry.Result);
        Assert.Null(entry.Reason);
        Assert.Equal("ledger-key-1", entry.LedgerKey);
    }

    [Fact]
    public async Task Append_Accumulates_OneEntryPerCall()
    {
        var log = new ImportLog(new FakeStore());

        await log.AppendAsync(Entry("Imported"));
        await log.AppendAsync(Entry("Skipped", "duplicate delivery"));
        await log.AppendAsync(Entry("Flagged", "path outside known Whisparr root"));

        var all = await log.LoadAllAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal(["Imported", "Skipped", "Flagged"], all.Select(e => e.Result));
        Assert.Equal("path outside known Whisparr root", all[2].Reason);
    }

    [Fact]
    public async Task CorruptBlob_LoadsEmpty_NeverThrows()
    {
        var store = new FakeStore();
        await store.SetAsync("importlog", "}{ not json");

        var log = new ImportLog(store);

        Assert.Empty(await log.LoadAllAsync());
        // A subsequent append still succeeds (the corrupt blob is treated as empty, then replaced).
        await log.AppendAsync(Entry("Imported"));
        Assert.Single(await log.LoadAllAsync());
    }
}
