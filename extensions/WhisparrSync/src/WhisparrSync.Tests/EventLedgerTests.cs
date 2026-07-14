using WhisparrSync.State;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// IMPT-03 idempotency contract for <see cref="EventLedger"/> over a fake store: a key is unseen until
/// recorded, a corrupt blob loads as empty (never throws), recording is idempotent, and the ONE
/// cross-channel key <see cref="EventLedger.ImportKey"/> is byte-stable across the webhook and poll
/// representations of the same import (the 03-02 overlap-dedup contract).
/// </summary>
public sealed class EventLedgerTests
{
    [Fact]
    public async Task SeenAsync_IsFalseUntilRecorded_ThenTrue()
    {
        var ledger = new EventLedger(new FakeStore());
        const string key = "abc123";

        Assert.False(await ledger.SeenAsync(key));
        await ledger.RecordAsync(key);
        Assert.True(await ledger.SeenAsync(key));
    }

    [Fact]
    public async Task CorruptBlob_LoadsEmpty_NeverThrows()
    {
        var store = new FakeStore();
        await store.SetAsync("eventledger", "{ this is not valid json ]");

        var ledger = new EventLedger(store);

        Assert.False(await ledger.SeenAsync("anything"));
        // A record after a corrupt read still succeeds (the corrupt blob is treated as empty, then replaced).
        await ledger.RecordAsync("anything");
        Assert.True(await ledger.SeenAsync("anything"));
    }

    [Fact]
    public async Task RecordAsync_IsIdempotent_NoDuplicateKeys()
    {
        var store = new FakeStore();
        var ledger = new EventLedger(store);

        await ledger.RecordAsync("k");
        await ledger.RecordAsync("k");

        Assert.True(await ledger.SeenAsync("k"));
        // The stored blob holds the key exactly once (no unbounded growth on redelivery).
        var blob = await store.GetAsync("eventledger");
        Assert.Equal("[\"k\"]", blob);
    }

    [Fact]
    public async Task TryClaimAsync_OnlyTheFirstCallerWins()
    {
        var ledger = new EventLedger(new FakeStore());
        const string key = "claim-me";

        Assert.True(await ledger.TryClaimAsync(key));  // first caller claims
        Assert.False(await ledger.TryClaimAsync(key)); // every later caller loses
        Assert.True(await ledger.SeenAsync(key));      // the claim recorded the key
    }

    [Fact]
    public async Task TryClaimAsync_ConcurrentRaceForSameKey_ExactlyOneWinner()
    {
        // The CR-01 regression: a webhook + poll racing the SAME import key must yield exactly ONE claim
        // winner (single ingest), NOT two. Separate ledger instances share the process-wide per-key gate, so
        // the check-and-insert is atomic across channels; before the fix an ungated SeenAsync let both win.
        var store = new FakeStore();
        const string key = "shared-import-key";

        var winners = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => new EventLedger(store).TryClaimAsync(key)));

        Assert.Equal(1, winners.Count(won => won)); // exactly one caller ingests; the rest skip
    }

    [Fact]
    public async Task ReleaseAsync_MakesAClaimedKeyClaimableAgain()
    {
        // A claimed-but-failed import must be retryable: releasing the claim lets the next delivery re-claim it.
        var ledger = new EventLedger(new FakeStore());
        const string key = "failed-ingest";

        Assert.True(await ledger.TryClaimAsync(key));
        await ledger.ReleaseAsync(key);
        Assert.False(await ledger.SeenAsync(key));    // the failed claim was released
        Assert.True(await ledger.TryClaimAsync(key)); // a retry can claim it again
    }

    [Fact]
    public void ImportKey_IsLowercaseHexSha256()
    {
        var key = EventLedger.ImportKey("dl-1", "/data/media/Scene.mkv");

        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void ImportKey_CrossChannel_WebhookAndHistory_ProduceIdenticalKey()
    {
        // The webhook carries movieFile.path (forward slashes); the /history record's importedPath may use
        // OS separators / a trailing slash / different case. Both go through NormalizePath before hashing, so
        // the same physical import derives ONE key regardless of channel (IMPT-03 / 03-02 overlap dedup).
        var webhook = EventLedger.ImportKey("ABCDEF0123", "/data/media/Scene (2024)/Scene.mkv");
        var history = EventLedger.ImportKey("ABCDEF0123", "\\data\\media\\Scene (2024)\\Scene.MKV\\");

        Assert.Equal(webhook, history);
    }

    [Fact]
    public void ImportKey_DiffersWhenDownloadIdOrPathDiffers()
    {
        var baseline = EventLedger.ImportKey("dl-1", "/data/a.mkv");

        Assert.NotEqual(baseline, EventLedger.ImportKey("dl-2", "/data/a.mkv"));
        Assert.NotEqual(baseline, EventLedger.ImportKey("dl-1", "/data/b.mkv"));
    }

    [Fact]
    public void NormalizePath_UnifiesSeparators_TrimsTrailing_AndCaseFolds()
    {
        Assert.Equal(
            EventLedger.NormalizePath("/data/Media/Scene.mkv"),
            EventLedger.NormalizePath("\\data\\media\\SCENE.MKV\\"));
    }
}
