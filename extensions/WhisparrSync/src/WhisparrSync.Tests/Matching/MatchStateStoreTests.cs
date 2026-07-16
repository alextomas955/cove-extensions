using WhisparrSync.Matching;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Matching;

/// <summary>
/// The persistence contract for <see cref="MatchStateStore"/> over a fake IExtensionStore:
/// round-trip, corrupt-blob resilience (empty, never throws), confirm promotes, reject suppresses, and
/// a string-rendered single-blob wire form keyed on the StashDB UUID.
/// </summary>
public sealed class MatchStateStoreTests
{
    private const string StoreKey = "matchstate";

    private static MatchState Entry(int coveId, int movieId, string stashId, MatchStatus status, MatchedBy leg = MatchedBy.StashId)
        => new(coveId, movieId, stashId, leg, MatchedAtUtcTicks: 638_000_000_000_000_000L, status);

    [Fact]
    public async Task RoundTrip_PreservesEntries()
    {
        var store = new FakeStore();
        var subject = new MatchStateStore(store);
        var entries = new[]
        {
            Entry(10, 1, "uuid-a", MatchStatus.Confirmed, MatchedBy.StashId),
            Entry(11, 2, "", MatchStatus.NeedsReview, MatchedBy.Tpdb),
        };

        await subject.SetAllAsync(entries);
        var loaded = await subject.LoadAllAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, e => e.CoveId == 10 && e.WhisparrMovieId == 1 && e.StashId == "uuid-a"
            && e.MatchedBy == MatchedBy.StashId && e.Status == MatchStatus.Confirmed
            && e.MatchedAtUtcTicks == 638_000_000_000_000_000L);
        Assert.Contains(loaded, e => e.CoveId == 11 && e.MatchedBy == MatchedBy.Tpdb
            && e.Status == MatchStatus.NeedsReview);
    }

    [Fact]
    public async Task CorruptBlob_LoadsEmpty_NeverThrows()
    {
        var store = new FakeStore();
        await store.SetAsync(StoreKey, "{ this is not valid json ][");

        var loaded = await new MatchStateStore(store).LoadAllAsync();

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Absent_LoadsEmpty()
    {
        var loaded = await new MatchStateStore(new FakeStore()).LoadAllAsync();
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Confirm_PromotesNeedsReviewEntry()
    {
        var store = new FakeStore();
        var subject = new MatchStateStore(store);
        var entry = Entry(10, 1, "uuid-a", MatchStatus.NeedsReview);
        await subject.SetAllAsync([entry]);

        await subject.ConfirmAsync(entry);
        var loaded = await subject.LoadAllAsync();

        var only = Assert.Single(loaded);
        Assert.Equal(MatchStatus.Confirmed, only.Status);
        Assert.Equal(10, only.CoveId);
    }

    [Fact]
    public async Task Reject_SuppressesEntry()
    {
        var store = new FakeStore();
        var subject = new MatchStateStore(store);
        var entry = Entry(10, 1, "uuid-a", MatchStatus.NeedsReview);
        await subject.SetAllAsync([entry]);

        await subject.RejectAsync(entry);
        var loaded = await subject.LoadAllAsync();

        Assert.Equal(MatchStatus.Rejected, Assert.Single(loaded).Status);
    }

    [Fact]
    public async Task Confirm_UpsertsWhenAbsent()
    {
        // W4 diff-persistence: a confirm validated against a fresh diff persists a brand-new link, so
        // GET /reconciliation reflects it after a reload without re-running preview-sync.
        var subject = new MatchStateStore(new FakeStore());

        await subject.ConfirmAsync(Entry(10, 1, "uuid-a", MatchStatus.NeedsReview));
        var loaded = await subject.LoadAllAsync();

        var only = Assert.Single(loaded);
        Assert.Equal(MatchStatus.Confirmed, only.Status);
        Assert.Equal(1, only.WhisparrMovieId);
    }

    [Fact]
    public async Task Blob_IsSingleKey_WithStringRenderedEnums()
    {
        var store = new FakeStore();
        var subject = new MatchStateStore(store);
        await subject.SetAllAsync([Entry(10, 1, "uuid-a", MatchStatus.NeedsReview, MatchedBy.Tpdb)]);

        var all = await store.GetAllAsync();
        Assert.Equal([StoreKey], all.Keys);

        var blob = all[StoreKey];
        Assert.Contains("\"Tpdb\"", blob);
        Assert.Contains("\"NeedsReview\"", blob);
        Assert.DoesNotContain("\"MatchedBy\":1", blob);
    }
}
