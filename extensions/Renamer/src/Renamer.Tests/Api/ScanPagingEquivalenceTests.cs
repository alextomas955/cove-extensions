using System.Text.Json;
using Renamer.Contracts;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

public sealed class ScanPagingEquivalenceTests
{
    private const int EntitiesPerKind = 40;
    private const int BigEntityFileCount = 5;

    // PathConfinement resolves every destination against a drive-rooted anchor on Windows, where a
    // driveless root therefore has no POSIX-literal spelling ("/dest/routed" resolves to "C:/dest/routed").
    // Per-platform roots keep the resolved target equal to the path the route asked for.
    private static readonly string LibRoot = OperatingSystem.IsWindows() ? "C:/lib" : "/lib";
    private static readonly string DestRoot = OperatingSystem.IsWindows() ? "C:/dest" : "/dest";
    private static readonly string RoutedDest = $"{DestRoot}/routed";

    // A title-only filename template with a studio-driven folder, so one fixture reaches both an
    // in-place rename and a folder move. The only-organized gate is on so an unorganized entity
    // reaches the gate branch - an empty title would not, because FilenameAsTitle falls back to the
    // basename.
    private static readonly RenamerOptions Options = new()
    {
        FilenameTemplate = "$title",
        FolderTemplate = "{$studio}",
        OnlyOrganized = true,
    };

    private static readonly RouteLookups Lookups = new(
        StudioIdToDest: new Dictionary<int, Destination>(),
        TagIdToDest: new Dictionary<int, Destination> { [101] = Dest.At(DestRoot, "routed") },
        PathExactToDest: new Dictionary<string, Destination>(),
        PathRegexRules: [],
        ExcludeTagIds: new HashSet<int> { 102 });

    // Seeds a fixture that reaches every planner branch: a plain rename, a multi-file rename, a
    // folder move, a routed cross-root move, a no-op, an excluded entity, a gate failure, a missing
    // source, and an occupied target that forces the suffix loop - across every renamable kind.
    private static FakeRenamerDataPort BuildFixture()
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryPaths(LibRoot, DestRoot);

        foreach (var kind in RenamableKinds.All)
        {
            // A distinct id block per kind, derived from its position so a kind added to the set gets
            // its own block instead of sharing one and colliding.
            int baseId = (RenamableKinds.All.IndexOf(kind) + 1) * 1000;
            string folder = $"{LibRoot}/{kind.ToString().ToLowerInvariant()}";
            var ids = new List<int>(EntitiesPerKind);

            for (int i = 1; i <= EntitiesPerKind; i++)
            {
                int id = baseId + i;
                ids.Add(id);

                string? title = $"Title{id}";
                string? studio = null;
                bool organized = true;
                var tags = new List<(int Id, string Name)>();
                string basename = $"raw{id}.mkv";
                int fileCount = 1;

                switch (i % 9)
                {
                    case 1:
                        break;                                        // plain in-place rename
                    case 2:
                        fileCount = BigEntityFileCount;               // more files than the smallest page
                        break;
                    case 3:
                        basename = $"Title{id}.mkv";                  // already at its computed destination
                        break;
                    case 4:
                        tags.Add((102, "skipme"));                    // excluded
                        break;
                    case 5:
                        organized = false;                            // fails the only-organized gate
                        break;
                    case 6:
                        port.SeedMissingSource($"{folder}/{basename}");
                        break;
                    case 7:
                        port.SeedOccupied(1, $"Title{id}.mkv", fileId: 999_999);
                        break;
                    case 8:
                        studio = "Acme";                              // folder template renders a subfolder
                        break;
                    default:
                        tags.Add((101, "routed"));                    // routed to another root
                        break;
                }

                var files = Enumerable.Range(0, fileCount)
                    .Select(f => new RenamerFile(
                        id * 10 + f, kind,
                        f == 0 ? basename : $"{Path.GetFileNameWithoutExtension(basename)}-{f}.mkv",
                        ParentFolderId: 1, folder))
                    .ToList();

                port.SeedEntity(new RenamerEntity(
                    id, kind, title, Code: null, studio, Date: null, organized,
                    Performers: [], TagRefs: tags, Files: files));
            }

            // Seeded out of ascending order on purpose: the walk's order must come from the port's
            // ascending contract, not from the order the fixture happened to insert.
            port.SeedAllIds(kind, [.. ids.OrderByDescending(x => x)]);
        }

        return port;
    }

    private static ScanRowPager NewPager(FakeRenamerDataPort port)
        => new(new RenamerPlanner(port), port);

    // The reference sequence: one full pass over every kind's ids in ascending order, planned
    // through the same planner the pager uses - the point of the comparison is the traversal, so
    // the planner is never doubled.
    private static async Task<List<ScanRow>> FullPlanAsync(FakeRenamerDataPort port)
    {
        var planner = new RenamerPlanner(port);
        var rows = new List<ScanRow>();

        foreach (var kind in RenamableKinds.All)
        {
            var ids = port.SeededIds(kind);
            var loaded = await port.LoadEntitiesAsync(kind, ids);
            var byId = loaded.ToDictionary(e => e.EntityId);

            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out var entity))
                {
                    var plan = await planner.PlanLoadedEntity(entity, Options, Lookups, default);
                    // The overflow flag is sourced exactly as the pager sources it - the same predicate,
                    // the same budget out of the same options, and no mount table on either side - so a
                    // difference between the two sequences can only be the traversal, which is what this
                    // class compares.
                    rows.AddRange(plan.Items.Select(item => ScanRow.From(
                        kind, plan.EntityId, item,
                        BatchPreview.InFlightPathOverflows(item, Options.FullPathMax))));
                }
            }
        }

        return rows;
    }

    private static async Task<List<ScanRow>> PagedWalkAsync(
        FakeRenamerDataPort port, int take, string? query = null, ScanBucketKind? bucket = null)
    {
        var pager = NewPager(port);
        var rows = new List<ScanRow>();
        ScanCursor? cursor = null;

        do
        {
            var page = await pager.PageAsync(RenamableKinds.All, cursor, take, query, bucket, Options, Lookups, default);
            Assert.True(page.EntitiesExamined <= ScanRowPager.MaxEntitiesPerRequest,
                $"page examined {page.EntitiesExamined} entities, over the {ScanRowPager.MaxEntitiesPerRequest} budget");
            rows.AddRange(page.Rows);
            cursor = page.Next;
        }
        while (cursor is not null);

        return rows;
    }

    // Compared as the serialized sequence, not field by field: a per-field loop would keep passing if a
    // field were added to the row and populated on only one of the two paths.
    private static string Wire(IEnumerable<ScanRow> rows)
        => JsonSerializer.Serialize(rows.ToArray(), PreviewContracts.PreviewResponseJsonOptions);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(500)]
    public async Task PagedWalk_EqualsOneFullPlan_AtEveryPageSize(int take)
    {
        var port = BuildFixture();

        string expected = Wire(await FullPlanAsync(port));
        string actual = Wire(await PagedWalkAsync(port, take));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task FixtureReachesEveryPlannerBranch()
    {
        var port = BuildFixture();
        var rows = await FullPlanAsync(port);

        var statuses = rows.Select(r => r.Status).ToHashSet();
        Assert.Contains(RenamerStatus.Rename, statuses);
        Assert.Contains(RenamerStatus.Move, statuses);
        Assert.Contains(RenamerStatus.NoOp, statuses);
        Assert.Contains(RenamerStatus.SkipExcluded, statuses);
        Assert.Contains(RenamerStatus.SkipGated, statuses);
        Assert.Contains(RenamerStatus.SkipMissingSource, statuses);
        Assert.Contains(rows, r => r.Suffixed);
        Assert.Contains(rows, r => r.NewFullPath.StartsWith(RoutedDest, StringComparison.Ordinal));
        Assert.Contains(rows, r => r.NewFullPath.Contains("/Acme/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    public async Task PagedWalk_UnderAPathSearch_EqualsTheSameSearchOverTheFullPlan(int take)
    {
        var port = BuildFixture();
        const string query = "Title10";

        string expected = Wire((await FullPlanAsync(port))
            .Where(r => ScanRowPager.Matches(r, ScanRowPager.NormalizeQuery(query))));
        string actual = Wire(await PagedWalkAsync(port, take, query: query));

        Assert.NotEqual("[]", actual);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(ScanBucketKind.WillChange)]
    [InlineData(ScanBucketKind.Attention)]
    [InlineData(ScanBucketKind.NoChange)]
    public async Task PagedWalk_UnderABucketFilter_EqualsThatBucketSelectedFromTheFullPlan(ScanBucketKind bucket)
    {
        var port = BuildFixture();

        string expected = Wire((await FullPlanAsync(port)).Where(r => ScanBucket.Of(r.Status) == bucket));
        string actual = Wire(await PagedWalkAsync(port, take: 7, bucket: bucket));

        Assert.NotEqual("[]", actual);
        Assert.Equal(expected, actual);
    }
}
