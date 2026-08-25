using Renamer.Contracts;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// The cursor walk that serves the whole-library dry run a page at a time: its traversal order, its
/// never-split-an-entity rule, its per-request entity budget, the server-side path search and bucket
/// filter, and its per-kind permission gate.
/// </summary>
[Trait("Tier", "L0")]
public sealed class ScanRowPagerTests
{
    private static readonly RouteLookups NoRoutes = new(
        new Dictionary<int, string>(), new Dictionary<int, string>(),
        new Dictionary<string, string>(), []);

    private static readonly RenamerOptions Options = new() { FilenameTemplate = "$title" };

    private static readonly RenamerFileKind[] AllKinds =
        [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio];

    /// <summary>Seeds <paramref name="ids"/> of <paramref name="kind"/>, each with <paramref name="filesPer"/> files.</summary>
    private static void Seed(
        FakeRenamerDataPort port, RenamerFileKind kind, IReadOnlyList<int> ids, int filesPer = 1)
    {
        foreach (var id in ids)
        {
            var files = Enumerable.Range(0, filesPer)
                .Select(f => new RenamerFile(
                    id * 100 + f, kind, $"raw{id}-{f}.mkv", 1, $"/lib/{kind}".ToLowerInvariant()))
                .ToList();
            port.SeedEntity(new RenamerEntity(
                id, kind, $"Title{id}", null, null, null, true, [], [], files));
        }

        port.SeedAllIds(kind, [.. ids]);
    }

    private static ScanRowPager NewPager(FakeRenamerDataPort port)
        => new(new RenamerPlanner(port), port);

    private static Task<ScanRowsPage> PageAsync(
        FakeRenamerDataPort port, IReadOnlyList<RenamerFileKind> kinds, ScanCursor? cursor, int take,
        string? query = null, ScanBucketKind? bucket = null)
        => NewPager(port).PageAsync(kinds, cursor, take, query, bucket, Options, NoRoutes, default);

    private static async Task<List<ScanRow>> WalkAsync(
        FakeRenamerDataPort port, IReadOnlyList<RenamerFileKind> kinds, int take,
        string? query = null, ScanBucketKind? bucket = null)
    {
        var rows = new List<ScanRow>();
        ScanCursor? cursor = null;
        do
        {
            var page = await PageAsync(port, kinds, cursor, take, query, bucket);
            rows.AddRange(page.Rows);
            cursor = page.Next;
        }
        while (cursor is not null);

        return rows;
    }

    [Fact]
    public async Task PageAsync_FromTheStart_ReturnsTheFirstRowsInWalkOrder_WithACursorPastThem()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [5, 1, 9, 7]);

        var page = await PageAsync(port, [RenamerFileKind.Video], null, take: 3);

        Assert.Equal([1, 5, 7], page.Rows.Select(r => r.EntityId));
        Assert.Equal(new ScanCursor(RenamerFileKind.Video, 7), page.Next);
        Assert.Equal(3, page.EntitiesExamined);
        Assert.False(page.BudgetExhausted);
    }

    [Fact]
    public async Task PageAsync_FollowingTheCursor_EnumeratesEveryRowExactlyOnce_AndEndsWithANullCursor()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [.. Enumerable.Range(1, 10)]);
        Seed(port, RenamerFileKind.Audio, [.. Enumerable.Range(50, 5)]);

        var rows = await WalkAsync(port, AllKinds, take: 3);

        Assert.Equal(15, rows.Count);
        Assert.Equal(rows.Select(r => (r.Kind, r.FileId)).Distinct().Count(), rows.Count);
        Assert.Equal(
            [.. Enumerable.Range(1, 10).Select(id => (RenamerFileKind.Video, id))
                .Concat(Enumerable.Range(50, 5).Select(id => (RenamerFileKind.Audio, id)))],
            rows.Select(r => (r.Kind, r.EntityId)));
    }

    [Fact]
    public async Task PageAsync_NeverSplitsAnEntity_EvenWhenItsFilesOvershootTake()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [1, 2], filesPer: 4);

        var page = await PageAsync(port, [RenamerFileKind.Video], null, take: 2);

        Assert.Equal(4, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.Equal(1, r.EntityId));
        Assert.Equal(1, page.EntitiesExamined);
    }

    [Fact]
    public async Task PageAsync_Query_MatchesEitherPathCaseInsensitively_AndTrims()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [1, 2, 3]);

        // The old path holds "raw2-0.mkv"; the rendered new path holds "Title2.mkv". Both sides of the
        // row must be searchable, and the query must trim and ignore case.
        var byOldPath = await WalkAsync(port, [RenamerFileKind.Video], take: 50, query: "  RAW2-0  ");
        var byNewName = await WalkAsync(port, [RenamerFileKind.Video], take: 50, query: "title3");

        Assert.Equal(2, Assert.Single(byOldPath).EntityId);
        Assert.Equal(3, Assert.Single(byNewName).EntityId);
    }

    [Fact]
    public async Task PageAsync_WhitespaceQuery_FiltersNothing()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [1, 2, 3]);

        var rows = await WalkAsync(port, [RenamerFileKind.Video], take: 50, query: "   ");

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task PageAsync_WillChangeBucket_ReturnsOnlyActingRows()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [1, 2]);
        // A missing source is an attention row; the other entity still renames.
        port.SeedMissingSource("/lib/video/raw2-0.mkv");

        var willChange = await WalkAsync(port, [RenamerFileKind.Video], take: 50, bucket: ScanBucketKind.WillChange);
        var attention = await WalkAsync(port, [RenamerFileKind.Video], take: 50, bucket: ScanBucketKind.Attention);
        var unfiltered = await WalkAsync(port, [RenamerFileKind.Video], take: 50);

        Assert.Equal(1, Assert.Single(willChange).EntityId);
        Assert.All(willChange, r => Assert.Equal(ScanBucketKind.WillChange, ScanBucket.Of(r.Status)));
        Assert.Equal(2, Assert.Single(attention).EntityId);
        Assert.Equal(2, unfiltered.Count);
    }

    [Fact]
    public async Task PageAsync_FilterMatchingNothingWithinTheBudget_ReportsBudgetExhausted_NotAnEmptyEnd()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [.. Enumerable.Range(1, ScanRowPager.MaxEntitiesPerRequest + 10)]);

        var page = await PageAsync(port, [RenamerFileKind.Video], null, take: 10, query: "no-such-path");

        Assert.Empty(page.Rows);
        Assert.NotNull(page.Next);
        Assert.True(page.BudgetExhausted);
        Assert.Equal(ScanRowPager.MaxEntitiesPerRequest, page.EntitiesExamined);
    }

    [Fact]
    public async Task PageAsync_OneKindReadable_NeverYieldsAnotherKindsRow_NorCursorsIntoIt()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [1, 2]);
        Seed(port, RenamerFileKind.Image, [3, 4]);
        Seed(port, RenamerFileKind.Audio, [5, 6]);

        var rows = await WalkAsync(port, [RenamerFileKind.Image], take: 1);

        Assert.All(rows, r => Assert.Equal(RenamerFileKind.Image, r.Kind));
        Assert.Equal([3, 4], rows.Select(r => r.EntityId));
    }

    [Fact]
    public async Task PageAsync_CursorNamingAnUnreadableKind_ResumesAtTheNextReadableKindsStart()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [1, 2]);
        Seed(port, RenamerFileKind.Audio, [5, 6]);

        // A stale/hand-crafted cursor pointing into Image, which this caller cannot read.
        var page = await PageAsync(
            port, [RenamerFileKind.Video, RenamerFileKind.Audio],
            new ScanCursor(RenamerFileKind.Image, 999), take: 50);

        Assert.Equal([5, 6], page.Rows.Select(r => r.EntityId));
        Assert.All(page.Rows, r => Assert.Equal(RenamerFileKind.Audio, r.Kind));
    }

    [Fact]
    public async Task PageAsync_CursorPastEveryReadableKind_ReturnsAnEmptyFinalPage()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [1, 2]);

        var page = await PageAsync(
            port, [RenamerFileKind.Video], new ScanCursor(RenamerFileKind.Audio, 0), take: 50);

        Assert.Empty(page.Rows);
        Assert.Null(page.Next);
        Assert.False(page.BudgetExhausted);
    }

    [Fact]
    public async Task PageAsync_TakeIsClampedToTheMaximum_AndNonPositiveFallsBackToTheDefault()
    {
        var port = new FakeRenamerDataPort();
        Seed(port, RenamerFileKind.Video, [.. Enumerable.Range(1, ScanRowPager.MaxTake + 50)]);

        var clamped = await PageAsync(port, [RenamerFileKind.Video], null, take: 100_000);
        var defaulted = await PageAsync(port, [RenamerFileKind.Video], null, take: 0);
        var negative = await PageAsync(port, [RenamerFileKind.Video], null, take: -5);

        Assert.Equal(ScanRowPager.MaxTake, clamped.Rows.Count);
        Assert.Equal(ScanRowPager.DefaultTake, defaulted.Rows.Count);
        Assert.Equal(ScanRowPager.DefaultTake, negative.Rows.Count);
    }

    [Fact]
    public async Task LoadEntityIdPageAsync_IsStrictlyAscendingAfterTheCursor_AndEmptyForANonRenamableKind()
    {
        var port = new FakeRenamerDataPort();
        port.SeedAllIds(RenamerFileKind.Video, 5, 1, 9, 7);

        Assert.Equal([1, 5, 7], await port.LoadEntityIdPageAsync(RenamerFileKind.Video, 0, 3));
        Assert.Equal([7, 9], await port.LoadEntityIdPageAsync(RenamerFileKind.Video, 5, 3));
        Assert.Empty(await port.LoadEntityIdPageAsync(RenamerFileKind.Video, 0, 0));
        Assert.Empty(await port.LoadEntityIdPageAsync(RenamerFileKind.Gallery, 0, 10));
        Assert.Empty(await port.LoadAllEntityIdsAsync(RenamerFileKind.Gallery));
    }
}
