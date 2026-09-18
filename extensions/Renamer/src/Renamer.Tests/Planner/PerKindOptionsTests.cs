using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// The per-entity-kind settings as the planner reads them: a kind turned off is skipped with a reason
/// rather than renamed, and a kind's own destination is the default its unmatched items take while a
/// matched routing rule still wins. PURE - no disk, no DB; every test asserts zero saves.
/// </summary>
public sealed class PerKindOptionsTests
{
    private static string SrcRoot => OperatingSystem.IsWindows() ? @"C:\library\incoming" : "/srv/library/incoming";
    private static string TextRoot => OperatingSystem.IsWindows() ? @"D:\documents" : "/mnt/documents";
    private static string DefaultRoot => OperatingSystem.IsWindows() ? @"G:\overflow" : "/mnt/overflow";
    private static string TagRoot => OperatingSystem.IsWindows() ? @"E:\by-tag" : "/mnt/by-tag";

    private static string Fwd(string p) => p.Replace('\\', '/');

    private static RenamerFile TextFile(int id) =>
        new(FileId: id, Kind: RenamerFileKind.Text, Basename: "raw.pdf", ParentFolderId: 5,
            ParentFolderPath: Fwd(SrcRoot), Format: "pdf");

    private static RenamerEntity TextEntity(params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Text, Title: "A Manual", Code: null, StudioName: "Acme",
            Date: null, Organized: true, Performers: [], TagRefs: [(7, "manual")], Files: files);

    private static FakeRenamerDataPort Port(params string[] libraryPaths)
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryPaths(libraryPaths);
        return port;
    }

    private static RenamerOptions MoveOptions() => new()
    {
        FilenameTemplate = "$title",
        FolderTemplate = "Sorted",
        FolderRoot = DefaultRoot,
        RequiredFields = [],
    };

    [Fact]
    public async Task ADisabledKind_SkipsEveryFileWithAReason()
    {
        var port = Port(SrcRoot, DefaultRoot);
        port.SeedEntity(TextEntity(TextFile(1), TextFile(2)));
        var planner = new RenamerPlanner(port);
        var options = MoveOptions() with
        {
            Kinds = new() { [RenamerFileKind.Text] = new KindOptions { Enabled = false } },
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Text, 10, options, default);

        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, i =>
        {
            Assert.Equal(RenamerStatus.SkipGated, i.Status);
            Assert.Contains("text", i.Reason!, StringComparison.Ordinal);
        });
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task DisablingOneKind_LeavesTheOthersRenaming()
    {
        var port = Port(SrcRoot, DefaultRoot);
        port.SeedEntity(TextEntity(TextFile(1)));
        var planner = new RenamerPlanner(port);
        var options = MoveOptions() with
        {
            Kinds = new() { [RenamerFileKind.Video] = new KindOptions { Enabled = false } },
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Text, 10, options, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task AKindDestination_ReplacesTheGlobalDefault_ForAnUnmatchedItem()
    {
        var port = Port(SrcRoot, DefaultRoot, TextRoot);
        port.SeedEntity(TextEntity(TextFile(1)));
        var planner = new RenamerPlanner(port);
        var options = MoveOptions() with
        {
            Kinds = new()
            {
                [RenamerFileKind.Text] = new KindOptions { Destination = Dest.At(TextRoot, "Manuals") },
            },
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Text, 10, options, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(TextRoot), item.ResolvedDestinationRoot);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task AMatchedRule_StillBeatsTheKindDestination()
    {
        var port = Port(SrcRoot, DefaultRoot, TextRoot, TagRoot);
        port.SeedEntity(TextEntity(TextFile(1)));
        var planner = new RenamerPlanner(port);
        var options = MoveOptions() with
        {
            Kinds = new()
            {
                [RenamerFileKind.Text] = new KindOptions { Destination = Dest.At(TextRoot, "Manuals") },
            },
        };

        // The routing maps reach the resolver through the per-batch lookups, not off the options.
        var lookups = new RouteLookups(
            new Dictionary<int, Destination>(),
            new Dictionary<int, Destination> { [7] = Dest.At(TagRoot, "Manuals") },
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            []);

        var plan = await planner.PlanAsync(RenamerFileKind.Text, 10, options, lookups, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(TagRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Tag:manual", item.MatchedRule);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task NoKindEntry_TakesTheGlobalDefault()
    {
        var port = Port(SrcRoot, DefaultRoot);
        port.SeedEntity(TextEntity(TextFile(1)));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Text, 10, MoveOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(DefaultRoot), item.ResolvedDestinationRoot);
        Assert.Empty(port.ApplyAndSaveCalls);
    }
}
