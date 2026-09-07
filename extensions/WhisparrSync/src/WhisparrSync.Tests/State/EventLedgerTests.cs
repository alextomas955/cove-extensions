using WhisparrSync.State;

namespace WhisparrSync.Tests.State;

/// <summary>
/// The idempotency contract for <see cref="EventLedger"/> over a fake store: a key is unseen until
/// recorded, a corrupt blob loads as empty (never throws), recording is idempotent, and the ONE
/// cross-channel key <see cref="EventLedger.ImportKey"/> is byte-stable across the webhook and poll
/// representations of the same import (the overlap-dedup contract).
/// </summary>
[Trait("Tier", "L0")]
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
        // Regression: a webhook + poll racing the SAME import key must yield exactly ONE claim
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
        // OS separators / a trailing slash — but the SAME case (both Whisparr channels report one path casing).
        // Both go through NormalizePath (separator-unify + trailing-trim) before hashing, so the same physical
        // import derives ONE key regardless of channel (overlap dedup).
        var webhook = EventLedger.ImportKey("ABCDEF0123", "/data/media/Scene (2024)/Scene.mkv");
        var history = EventLedger.ImportKey("ABCDEF0123", "\\data\\media\\Scene (2024)\\Scene.mkv\\");

        Assert.Equal(webhook, history);
    }

    [Fact]
    public void ImportKey_IsCaseSensitive_DistinctFilesDoNotCollide()
    {
        // On the Linux/Docker target /data/media/A.mkv and /data/media/a.mkv are DIFFERENT files, so
        // their idempotency keys must differ — case-folding here would skip the second file as a false duplicate.
        Assert.NotEqual(
            EventLedger.ImportKey("dl-1", "/data/media/A.mkv"),
            EventLedger.ImportKey("dl-1", "/data/media/a.mkv"));
    }

    [Fact]
    public void ImportKey_DiffersWhenDownloadIdOrPathDiffers()
    {
        var baseline = EventLedger.ImportKey("dl-1", "/data/a.mkv");

        Assert.NotEqual(baseline, EventLedger.ImportKey("dl-2", "/data/a.mkv"));
        Assert.NotEqual(baseline, EventLedger.ImportKey("dl-1", "/data/b.mkv"));
    }

    [Fact]
    public void NormalizePath_UnifiesSeparators_TrimsTrailing_CaseSensitive()
    {
        // Separators unify and a trailing slash is trimmed, but case is PRESERVED.
        Assert.Equal(
            EventLedger.NormalizePath("/data/Media/Scene.mkv"),
            EventLedger.NormalizePath("\\data\\Media\\Scene.mkv\\"));

        // A case difference is NOT normalized away — two distinct files stay distinct.
        Assert.NotEqual(
            EventLedger.NormalizePath("/data/Media/Scene.mkv"),
            EventLedger.NormalizePath("/data/media/scene.mkv"));
    }

    [Fact]
    public async Task PruneHistBelow_DropsHistKeysAtOrBelowCheckpoint_KeepsHigherHistAndImportKeys()
    {
        var store = new FakeStore();
        var ledger = new EventLedger(store);
        var importKey = EventLedger.ImportKey("dl-1", "/data/media/A.mkv");
        await ledger.RecordAsync("hist:3");
        await ledger.RecordAsync("hist:5");
        await ledger.RecordAsync("hist:8");
        await ledger.RecordAsync(importKey);

        await ledger.PruneHistBelowAsync(5);

        Assert.False(await ledger.SeenAsync("hist:3")); // below the checkpoint → provably dead, pruned
        Assert.False(await ledger.SeenAsync("hist:5")); // at the checkpoint → provably dead, pruned
        Assert.True(await ledger.SeenAsync("hist:8"));  // above the checkpoint → still live
        Assert.True(await ledger.SeenAsync(importKey)); // a cross-channel ImportKey is untouched by the hist prune
    }

    [Fact]
    public async Task PruneHistBelow_WritesNothing_WhenNoHistKeyIsAtOrBelowTheMark()
    {
        var store = new FakeStore();
        var ledger = new EventLedger(store);
        await ledger.RecordAsync("hist:9");
        var writesBefore = store.SetCallCount;

        await ledger.PruneHistBelowAsync(5);

        Assert.Equal(writesBefore, store.SetCallCount); // a no-op prune must not rewrite the blob
        Assert.True(await ledger.SeenAsync("hist:9"));
    }

    [Fact]
    public async Task ImportKeyWindow_EvictsOldestKeys_OverTheCap_KeepingNewest()
    {
        var store = new FakeStore();
        var ledger = new EventLedger(store);
        var cap = EventLedger.MaxImportKeys;

        // Evicting the oldest keys past the cap is safe: an evicted key only re-runs an idempotent re-import
        // (the host unique index prevents a duplicate entity), never a still-queryable guarantee lost.
        for (var i = 0; i < cap + 5; i++)
        {
            await ledger.RecordAsync(EventLedger.ImportKey($"dl-{i}", $"/data/media/{i}.mkv"));
        }

        Assert.False(await ledger.SeenAsync(EventLedger.ImportKey("dl-0", "/data/media/0.mkv")));         // oldest → evicted
        Assert.False(await ledger.SeenAsync(EventLedger.ImportKey("dl-4", "/data/media/4.mkv")));         // still in the evicted run
        Assert.True(await ledger.SeenAsync(EventLedger.ImportKey("dl-5", "/data/media/5.mkv")));          // first survivor
        Assert.True(await ledger.SeenAsync(
            EventLedger.ImportKey($"dl-{cap + 4}", $"/data/media/{cap + 4}.mkv")));                        // newest → retained
    }

    [Fact]
    public async Task ImportKeyWindow_DoesNotEvictHistKeys_EvenBeyondTheCap()
    {
        // hist: keys are bounded by the checkpoint prune, not the ImportKey window — a full ImportKey window
        // must never shed a live hist: key (it would defeat the cheap re-poll no-op an incomplete pass relies on).
        var store = new FakeStore();
        var ledger = new EventLedger(store);
        await ledger.RecordAsync("hist:999999");
        for (var i = 0; i < EventLedger.MaxImportKeys + 3; i++)
        {
            await ledger.RecordAsync(EventLedger.ImportKey($"dl-{i}", $"/data/media/{i}.mkv"));
        }

        Assert.True(await ledger.SeenAsync("hist:999999")); // survived a full-window eviction of ImportKeys
    }
}
