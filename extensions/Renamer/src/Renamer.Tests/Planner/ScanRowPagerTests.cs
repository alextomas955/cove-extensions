using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// The cursor walk that serves the whole-library dry run a page at a time: its traversal order, its
/// never-split-an-entity rule, its per-request entity budget, the server-side path search and bucket
/// filter, and its per-kind permission gate - plus the per-row in-flight overflow flag as a PAGE reads
/// it, driven through <see cref="ScanRowPager.PageAsync"/> rather than through <see cref="ScanRow.From"/>,
/// because the projection classifies nothing itself and a test of it would only re-check the value it was
/// handed. What is at stake in that last case is the composition: that the page computes the flag at all,
/// and against the same budget the planner just planned against.
/// </summary>
public sealed class ScanRowPagerTests
{
    private static readonly RouteLookups NoRoutes = new(
        new Dictionary<int, Destination>(), new Dictionary<int, Destination>(),
        new Dictionary<string, Destination>(), []);

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

    // The in-flight overflow flag, as a page reads it.
    // PURE: a fake port, string-only path math and a synthetic mount table. No disk, no DB, and no
    // dependence on the runner's own volumes.

    // Volume identity is per-platform: the path root on Windows, the enclosing mount point on Unix. Both
    // spellings are FOUR characters, which is what lets one arithmetic land on the same absolute length on
    // both - a Windows root is otherwise longer than its Unix twin.
    private static string SourceRoot => OperatingSystem.IsWindows() ? @"C:\s" : "/sss";
    private static string DestRoot => OperatingSystem.IsWindows() ? @"D:\d" : "/ddd";

    // On Unix every path under "/" shares one volume, so the destination root has to BE a mount for the
    // cross-volume arm to exist. On Windows the drive letters already carry that difference.
    private static readonly IReadOnlyCollection<string>? Mounts =
        OperatingSystem.IsWindows() ? null : ["/", "/ddd"];

    // Above the shipped default nothing would fit; well under it the field-dropping reducer stays out of
    // the way. Every length below is positioned relative to this and to the minted segment's own
    // declaration.
    private const int Budget = 60;

    private const string Folder = "S";

    // root(4) + "/" + Folder(1) + "/" - the fixed part of every resolved path below. Hand-counted, and
    // asserted against the planner's own output before any flag is read.
    private const int PathPrefixLength = 7;

    private const string Extension = ".mkv";

    /// <summary>A title whose rendered absolute path is exactly <paramref name="pathLength"/> characters.</summary>
    private static string TitleForPathLength(int pathLength) =>
        new('a', pathLength - PathPrefixLength - Extension.Length);

    private const int SourceStudioId = 42;
    private const int DestStudioId = 43;

    private static RenamerEntity OverflowEntity(int entityId, string title, int studioId) =>
        new(EntityId: entityId, Kind: RenamerFileKind.Video, Title: title, Code: null,
            StudioName: "Acme", Date: null, Organized: true, Performers: [], TagRefs: [],
            Files: [new RenamerFile(
                entityId, RenamerFileKind.Video, $"raw{entityId}{Extension}", 5,
                SourceRoot.Replace('\\', '/'))],
            StudioId: studioId);

    private static readonly RenamerOptions OverflowOptions = new()
    {
        FilenameTemplate = "$title",
        AllowedRoots = [SourceRoot, DestRoot],
        FullPathMax = Budget,
    };

    // Routing by studio, so one fixture reaches a cross-volume destination and a same-volume one without
    // the two differing in any other way.
    private static RouteLookups OverflowLookups() =>
        new(
            new Dictionary<int, Destination>
            {
                [DestStudioId] = Dest.At(DestRoot, Folder),
                [SourceStudioId] = Dest.At(SourceRoot, Folder),
            },
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            []);

    [Fact]
    public async Task PagedRows_CarryTheOverflowFlag_OnlyForTheCrossVolumeRowPastTheBoundary()
    {
        // The longest final path whose cross-volume copy still fits: the copy is minted
        // CrossVolumeMover.InFlightSuffixLength characters longer beside the destination before being
        // promoted, and the planner budgets only the final path.
        int longestThatFits = Budget - CrossVolumeMover.InFlightSuffixLength;

        var port = new FakeRenamerDataPort();
        port.SeedLibraryPaths(SourceRoot, DestRoot);
        port.SeedEntity(OverflowEntity(1, TitleForPathLength(Budget), DestStudioId));
        port.SeedEntity(OverflowEntity(2, TitleForPathLength(longestThatFits), DestStudioId));
        port.SeedEntity(OverflowEntity(3, TitleForPathLength(Budget), SourceStudioId));
        port.SeedAllIds(RenamerFileKind.Video, [1, 2, 3]);

        var pager = new ScanRowPager(new RenamerPlanner(port), port, Mounts);
        var page = await pager.PageAsync(
            [RenamerFileKind.Video], cursor: null, take: 10, query: null, bucket: null,
            OverflowOptions, OverflowLookups(), default);

        var byFileId = page.Rows.ToDictionary(r => r.FileId);
        Assert.Equal(3, byFileId.Count);

        // The lengths and the volumes the three rows claim, read off the planner's own output. Without
        // these a slip in the path arithmetic would put every row on the same side of the boundary and
        // leave three false flags looking like a correct answer.
        Assert.Equal(Budget, byFileId[1].NewFullPath.Length);
        Assert.Equal(longestThatFits, byFileId[2].NewFullPath.Length);
        Assert.Equal(Budget, byFileId[3].NewFullPath.Length);
        Assert.False(VolumeClassifier.SameVolume(byFileId[1].OldFullPath, byFileId[1].NewFullPath, Mounts));
        Assert.True(VolumeClassifier.SameVolume(byFileId[3].OldFullPath, byFileId[3].NewFullPath, Mounts));

        // Every row is a real acting plan, so no flag below is false merely because its row was skipped.
        Assert.All(page.Rows, r => Assert.Equal(RenamerStatus.Move, r.Status));

        Assert.True(byFileId[1].InFlightPathOverflow);
        Assert.False(byFileId[2].InFlightPathOverflow);
        Assert.False(byFileId[3].InFlightPathOverflow);
    }
}
