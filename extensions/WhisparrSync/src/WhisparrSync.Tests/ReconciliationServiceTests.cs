using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Matching;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// The MATCH-03 reconciliation diff proven read-only over the 02-01 fakes: a StashDB pair classifies
/// matched, an orphan Whisparr movie unmatched, a title/year near-match needs-review, counts sum to the
/// movie total; the diff is incremental across a pre-seeded map (confirmed kept, rejected suppressed);
/// and a plain reconcile writes NOTHING to the store (zero mutation).
/// </summary>
public sealed class ReconciliationServiceTests
{
    private static CoveVideo Cove(int id, string? title = null, DateOnly? date = null, string[]? stashIds = null)
        => new(id, title, date, stashIds ?? [], [], []);

    private static WhisparrMovie Movie(int id, string? title = null, int? year = null, string? stashId = null, string? itemType = null)
        => new(id, title, year, stashId, ForeignId: null, itemType, Monitored: false, HasFile: false, MovieFile: null);

    private static MatchState Entry(int coveId, int movieId, string stashId, MatchStatus status, MatchedBy leg)
        => new(coveId, movieId, stashId, leg, MatchedAtUtcTicks: 638_000_000_000_000_000L, status);

    [Fact]
    public void ReconciliationDiff_ClassifiesMatchedUnmatchedNeedsReview()
    {
        var cove = new[]
        {
            Cove(10, stashIds: ["uuid-a"]),
            Cove(11, title: "The Great Scene", date: new DateOnly(2021, 1, 1)),
        };
        var movies = new[]
        {
            Movie(1, stashId: "uuid-a", itemType: "scene"),   // matched (StashDB)
            Movie(2, title: "Great Scene", year: 2021),        // needs-review (fuzzy)
            Movie(3, title: "Orphan", year: 1999),             // unmatched
        };

        var diff = ReconciliationService.Reconcile(cove, movies, []);

        Assert.Equal(1, Assert.Single(diff.Matched).Movie.Id);
        Assert.Equal(2, Assert.Single(diff.NeedsReview).Movie.Id);
        Assert.Equal(3, Assert.Single(diff.Unmatched).Movie.Id);
        Assert.Equal(new ReconciliationCounts(Matched: 1, Unmatched: 1, NeedsReview: 1, Total: 3), diff.Counts);
    }

    [Fact]
    public void ReconciliationDiff_Incremental_KeepsConfirmedPair()
    {
        // Movie 5 has no derivable link (no shared StashDB id, no path, no fuzzy), but a Confirmed map
        // entry pins it to Cove 10 — it stays matched on re-run without being re-derived.
        var cove = new[] { Cove(10, title: "Pinned", date: new DateOnly(2020, 1, 1)) };
        var movies = new[] { Movie(5, title: "Nothing Alike", year: 2005) };
        var persisted = new[] { Entry(10, 5, "uuid-x", MatchStatus.Confirmed, MatchedBy.Fuzzy) };

        var diff = ReconciliationService.Reconcile(cove, movies, persisted);

        var matched = Assert.Single(diff.Matched);
        Assert.Equal(5, matched.Movie.Id);
        Assert.Equal(10, matched.MatchedVideo!.CoveId);
        Assert.Empty(diff.NeedsReview);
        Assert.Empty(diff.Unmatched);
    }

    [Fact]
    public void ReconciliationDiff_Incremental_SuppressesRejectedFuzzySuggestion()
    {
        var cove = new[] { Cove(11, title: "The Great Scene", date: new DateOnly(2021, 1, 1)) };
        var movies = new[] { Movie(2, title: "Great Scene", year: 2021) };   // would be a fuzzy suggestion
        var persisted = new[] { Entry(11, 2, "", MatchStatus.Rejected, MatchedBy.Fuzzy) };

        var diff = ReconciliationService.Reconcile(cove, movies, persisted);

        Assert.Empty(diff.NeedsReview);
        Assert.Equal(2, Assert.Single(diff.Unmatched).Movie.Id);
    }

    [Fact]
    public void ReconciliationDiff_ConfirmedPair_WithDeletedCoveVideo_IsNotMatched()
    {
        // A Confirmed entry pins Movie 5 to Cove 10, but Cove 10 no longer exists in the library (deleted
        // between confirm and re-run). The row must NOT be reported as Matched with a null Cove video — it
        // degrades to unmatched and does not inflate the matched count (WR-02).
        var cove = new[] { Cove(99, title: "Still Here", date: new DateOnly(2020, 1, 1)) };
        var movies = new[] { Movie(5, title: "Nothing Alike", year: 2005) };
        var persisted = new[] { Entry(10, 5, "uuid-x", MatchStatus.Confirmed, MatchedBy.Fuzzy) };

        var diff = ReconciliationService.Reconcile(cove, movies, persisted);

        Assert.Empty(diff.Matched);
        var unmatched = Assert.Single(diff.Unmatched);
        Assert.Equal(5, unmatched.Movie.Id);
        Assert.Null(unmatched.MatchedVideo);
        Assert.False(unmatched.AutoApplies);
        Assert.Equal(new ReconciliationCounts(Matched: 0, Unmatched: 1, NeedsReview: 0, Total: 1), diff.Counts);
    }

    [Fact]
    public async Task ReconciliationDiff_PlainReconcile_WritesNothingToStore()
    {
        var store = new FakeStore();
        var matchStore = new MatchStateStore(store);
        var port = new FakeCoveLibraryPort();
        port.Seed(Cove(10, stashIds: ["uuid-a"]));
        var coveVideos = await port.LoadAllVideosAsync();
        var movies = new[] { Movie(1, stashId: "uuid-a", itemType: "scene") };
        var persisted = await matchStore.LoadAllAsync();

        var diff = ReconciliationService.Reconcile(coveVideos, movies, persisted);

        Assert.Single(diff.Matched);
        Assert.Equal(0, store.SetCallCount);   // zero-mutation proof: a plain reconcile persists nothing
    }
}
