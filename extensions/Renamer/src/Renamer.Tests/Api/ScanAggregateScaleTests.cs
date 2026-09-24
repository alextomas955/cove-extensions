using Cove.Plugins;
using Renamer.Contracts;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

public sealed class ScanAggregateScaleTests
{
    private const int SmallFixture = 10;
    private const int LargeFixture = 10_000;

    // The ceiling the stored blob must stay under, derived from the shape rather than asserted as a
    // number: per kind, one status entry per RenamerStatus member plus at most
    // MaxVolumePairsPerKind volume-pair entries, each entry generously allowed this many bytes of
    // names, digits and JSON punctuation.
    private const int BytesPerEntry = 160;

    private static int Ceiling(int kinds) =>
        512 + (kinds * BytesPerEntry
            * (Enum.GetValues<RenamerStatus>().Length + ScanSummary.MaxVolumePairsPerKind + 8));

    // An extension wired with a store and nothing else. InitializeAsync is deliberately skipped:
    // the scan core takes its data port as a parameter and needs neither the scope factory nor the
    // event bus, so leaving the host seams uncaptured keeps this class free of any Cove source type
    // and therefore compiled and runnable on the cove-absent CI leg.
    private static (global::Renamer.Renamer Ext, FakeStore Store) NewExtension()
    {
        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        ((IStatefulExtension)ext).SetStore(store);
        return (ext, store);
    }

    // Seeds files single-file entities spread evenly across the renamable kinds; every entity
    // renames in place, so the aggregate's acting counts are the file count.
    private static FakeRenamerDataPort SeedLibrary(int files, string folder = "/lib")
    {
        var port = new FakeRenamerDataPort();
        int perKind = files / RenamableKinds.All.Length;
        int remainder = files % RenamableKinds.All.Length;

        for (int k = 0; k < RenamableKinds.All.Length; k++)
        {
            var kind = RenamableKinds.All[k];
            int count = perKind + (k < remainder ? 1 : 0);
            var ids = new List<int>(count);

            for (int i = 1; i <= count; i++)
            {
                int id = (k + 1) * 1_000_000 + i;
                ids.Add(id);
                port.SeedEntity(new RenamerEntity(
                    id, kind, $"Title{id}", null, null, null, true, [], [],
                    [new RenamerFile(id, kind, $"raw{id}.mkv", 1, folder, SizeBytes: 1024)]));
            }

            port.SeedAllIds(kind, [.. ids]);
        }

        return port;
    }

    // Nothing is denied here: this suite's subject is that the stored aggregate stays bounded.
    private static readonly global::Renamer.AllowedIds AllowAll = (_, ids, _) => Task.FromResult(ids);

    private static async Task<(string Json, ScanSummary Summary, FakeStore Store)> ScanAsync(
        FakeRenamerDataPort port, RenamerOptions? options = null)
    {
        var (ext, store) = NewExtension();
        await ext.RunScanCoreAsync(
            port, RenamableKinds.All, options ?? new RenamerOptions { FilenameTemplate = "$title" },
            AllowAll,
            new FakeJobProgress(), default);

        // Read the raw stored string: parsing is exactly what would hide growth, and the byte length is
        // the thing that broke.
        string json = (await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey))!;
        var summary = System.Text.Json.JsonSerializer.Deserialize<ScanSummary>(
            json, PreviewContracts.PreviewResponseJsonOptions)!;
        return (json, summary, store);
    }

    [Theory]
    [InlineData(SmallFixture)]
    [InlineData(LargeFixture)]
    public async Task StoredBlob_StaysUnderTheDerivedCeiling(int files)
    {
        var (json, summary, _) = await ScanAsync(SeedLibrary(files));

        int ceiling = Ceiling(summary.Kinds.Count);
        Assert.True(json.Length < ceiling,
            $"{files}-file scan stored {json.Length} bytes, over the derived ceiling of {ceiling}");
    }

    [Fact]
    public async Task StoredBlob_IsNoBiggerAtTenThousandFilesThanAtTen()
    {
        var (smallJson, smallSummary, _) = await ScanAsync(SeedLibrary(SmallFixture));
        var (largeJson, _, _) = await ScanAsync(SeedLibrary(LargeFixture));

        Assert.True(largeJson.Length - smallJson.Length < Ceiling(smallSummary.Kinds.Count),
            $"stored blob grew from {smallJson.Length} to {largeJson.Length} bytes over 1000x the files");
    }

    [Fact]
    public async Task StoredAggregate_IsExactAtTenThousandFiles()
    {
        var (_, summary, _) = await ScanAsync(SeedLibrary(LargeFixture));

        Assert.Equal(LargeFixture, summary.Kinds.Sum(k => k.Files));
        Assert.Equal(LargeFixture, summary.Kinds.Sum(k => k.Entities));
        Assert.Equal(LargeFixture, summary.Kinds.Sum(int (ScanKindSummary k) => k.StatusCounts.Sum(int (ScanStatusCount c) => c.Count)));
        Assert.All(summary.Kinds, (ScanKindSummary k) => Assert.Equal<int>(k.Files, k.StatusCounts.Sum(c => c.Count)));
    }

    [Fact]
    public async Task TenThousandFileScan_WalksPages_AndNeverAsksForEveryIdAtOnce()
    {
        var port = SeedLibrary(LargeFixture);
        await ScanAsync(port);

        int perKind = LargeFixture / RenamableKinds.All.Length;

        // That no whole-kind read happens is the compiler's job now: IRenamerDataPort offers none.
        // What is left to assert is that the pages are narrow and that there are several per kind.
        Assert.True(port.IdPageRequests.Count > RenamableKinds.All.Length,
            "a kind of 2500 entities cannot be walked in one page");
        Assert.All(port.IdPageRequests, r => Assert.InRange(r.Take, 1, perKind - 1));
    }

    [Fact]
    public async Task CompletedScan_WritesTheStoreExactlyOnce()
    {
        var (_, _, store) = await ScanAsync(SeedLibrary(LargeFixture));

        Assert.Equal(1, store.SetCallCount);
    }

    [Fact]
    public async Task Readback_BucketCountsEqualTheBucketsDerivedFromTheExactStatusCounts()
    {
        var (_, summary, _) = await ScanAsync(SeedLibrary(LargeFixture));

        var view = ScanSummaryView.From(summary, RenamableKinds.All);

        int Expected(ScanBucketKind bucket) => summary.Kinds
            .SelectMany(k => k.StatusCounts)
            .Where(c => ScanBucket.Of(c.Status) == bucket)
            .Sum(c => c.Count);

        Assert.Equal(Expected(ScanBucketKind.WillChange), view.WillChange);
        Assert.Equal(Expected(ScanBucketKind.Attention), view.Attention);
        Assert.Equal(Expected(ScanBucketKind.NoChange), view.NoChange);
        Assert.Equal(LargeFixture, view.WillChange + view.Attention + view.NoChange);
        Assert.Equal(LargeFixture, view.TotalFiles);
    }
}
