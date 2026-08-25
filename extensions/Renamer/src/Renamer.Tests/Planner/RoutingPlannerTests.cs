using System.Text.RegularExpressions;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the resolver is wired into <c>RenamerPlanner.PlanAsync</c>: a routed entity produces
/// a Move whose <see cref="RenamerPlanItem.ResolvedDestinationRoot"/> / <see cref="RenamerPlanItem.MatchedRule"/>
/// / <see cref="RenamerPlanItem.TargetVolume"/> reflect the matched route, and confinement is anchored
/// on the routed root (so the move lands on the destination volume). A no-route entity (empty maps)
/// stays source-confined exactly as before. Default-relocate is proven DISABLED end-to-end through
/// the planner. PURE — no disk, no DB; every test asserts zero <c>SaveAsync</c> calls.
/// </summary>
[Trait("Tier", "L0")]
public sealed class RoutingPlannerTests
{
    // OS-aware absolute roots (path-syntax valid on the current OS), mirroring PathConfinementAllowlistTests.
    private static string SrcRoot => OperatingSystem.IsWindows() ? @"C:\library\incoming" : "/srv/library/incoming";
    private static string StudioRoot => OperatingSystem.IsWindows() ? @"D:\studios\acme" : "/mnt/studios/acme";
    private static string TagRoot => OperatingSystem.IsWindows() ? @"E:\by-tag\anime" : "/mnt/by-tag/anime";
    private static string PathRoot => OperatingSystem.IsWindows() ? @"F:\by-source" : "/mnt/by-source";
    private static string DefaultRoot => OperatingSystem.IsWindows() ? @"G:\overflow" : "/mnt/overflow";
    private static string UnorgRoot => OperatingSystem.IsWindows() ? @"H:\unsorted" : "/mnt/unsorted";

    private static string Fwd(string p) => p.Replace('\\', '/');

    private static RenamerFile VideoFile(int id, string basename, string folderPath) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: 5,
            ParentFolderPath: Fwd(folderPath), Format: "mkv", Width: 1920, Height: 1080,
            Duration: 3600, VideoCodec: "h264", AudioCodec: "aac", FrameRate: 30);

    private static RenamerEntity Entity(
        params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: "ABC-1", StudioName: "Acme",
            Date: new DateOnly(2024, 3, 2), Organized: true,
            Performers: [new RenamerPerformer(1, "Bob", false, null)], TagRefs: [(7, "anime")], Files: files);

    // A move-producing render: a non-empty folder template makes isMove true, so the routed root is
    // the confinement anchor and the absolute target lands on the destination volume.
    private static RenamerOptions MoveOptions(List<string> roots) =>
        new() { FilenameTemplate = "$title", FolderTemplate = "Sorted", AllowedRoots = roots };

    private static RouteLookups Lookups(
        IReadOnlyDictionary<int, string>? studio = null,
        IReadOnlyDictionary<int, string>? tag = null,
        IReadOnlyDictionary<string, string>? exact = null,
        IReadOnlyList<(Regex, string)>? regex = null,
        IReadOnlySet<int>? excludeTags = null,
        IReadOnlySet<int>? excludeStudios = null,
        IReadOnlySet<string>? excludePathsExact = null,
        IReadOnlyList<Regex>? excludePathRegex = null) =>
        new(
            studio ?? new Dictionary<int, string>(),
            tag ?? new Dictionary<int, string>(),
            exact ?? new Dictionary<string, string>(StringComparer.Ordinal),
            regex ?? Array.Empty<(Regex, string)>(),
            excludeTags, excludeStudios, excludePathsExact, excludePathRegex);

    [Fact]
    public async Task StudioRouted_CarriesRootRuleAndVolume()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions([SrcRoot, StudioRoot]);
        var lk = Lookups(studio: new Dictionary<int, string> { [42] = StudioRoot });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(StudioRoot, item.ResolvedDestinationRoot);
        Assert.Equal("Studio:42(direct)", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(StudioRoot), item.TargetVolume);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task StudioRouted_EmptyFolderTemplate_StillMovesToRoutedRoot()
    {
        // A matched route relocates the file even when the folder template is empty: the user wants
        // the routed studio's files dropped at the root of the destination, with no subfolder. The
        // move must land on the destination volume's root, not silently renamer in place under the
        // source folder. (Every other routed test here pairs the route with a non-empty folder
        // template, which is why this empty-template path needs its own guard.)
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "",                 // no subfolder — the route alone must drive the move
            AllowedRoots = [SrcRoot, StudioRoot],
        };
        var lk = Lookups(studio: new Dictionary<int, string> { [42] = StudioRoot });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(StudioRoot, item.ResolvedDestinationRoot);
        Assert.Equal("Studio:42(direct)", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(StudioRoot), item.TargetVolume);
        // The file lands at the ROOT of the routed destination (no subfolder), NOT under its source.
        Assert.Equal(Fwd(StudioRoot) + "/My Film.mkv", item.NewFullPath);
        Assert.DoesNotContain("incoming", item.NewFullPath);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task TagRouted_MatchesTheRenamedTag_AndReportsItsCurrentName()
    {
        var port = new FakeRenamerDataPort();
        // The rule was written when tag 7 was called something else. It routes on the id, so the
        // rename cannot break it, and the reason shows the name the tag carries NOW.
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { TagRefs = [(7, "Anime (renamed)")] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions([SrcRoot, TagRoot]);
        var lk = Lookups(tag: new Dictionary<int, string> { [7] = TagRoot });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(TagRoot, item.ResolvedDestinationRoot);
        Assert.Equal("Tag:Anime (renamed)", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(TagRoot), item.TargetVolume);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task SourcePathRouted_Exact_CarriesRootAndRule()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions([SrcRoot, PathRoot]);
        var lk = Lookups(exact: new Dictionary<string, string>(StringComparer.Ordinal) { [Fwd(SrcRoot)] = PathRoot });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(PathRoot, item.ResolvedDestinationRoot);
        Assert.Equal("SourcePath:exact", item.MatchedRule);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task UnorganizedRouted_ProducesMove_NotSkip()
    {
        var port = new FakeRenamerDataPort();
        // Organized=false + an UnorganizedDestination set → routes to it, does not gate to a skip.
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { Organized = false, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            AllowedRoots = [SrcRoot, UnorgRoot],
            UnorganizedDestination = UnorgRoot,
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(UnorgRoot, item.ResolvedDestinationRoot);
        Assert.Equal("Unorganized", item.MatchedRule);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task SourceConfine_EmptyMaps_LegacyAnchor_NullRoot()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        // Empty lookups + empty maps + no allowed roots → legacy source-confine: anchored on the file's
        // own folder, ResolvedDestinationRoot null.
        var opts = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "Sorted" };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Null(item.ResolvedDestinationRoot);
        Assert.Equal("InPlace", item.MatchedRule);
        // The move lands under the file's own source folder, exactly as before this phase.
        Assert.EndsWith("library/incoming/Sorted/My Film.mkv", item.NewFullPath);
        // A source-confine item has no destination volume of interest (in-place move), so
        // TargetVolume is empty — never the fictitious synthetic-anchor root.
        Assert.Equal("", item.TargetVolume);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task DefaultRelocateDisabled_NoRelocate_StaysSourceConfined()
    {
        var port = new FakeRenamerDataPort();
        // Unmatched entity (no studio/tag/path rule), a DefaultDestination set, but the flag OFF.
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 999, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            AllowedRoots = [SrcRoot, DefaultRoot],
            DefaultDestination = DefaultRoot,
            EnableDefaultRelocate = false,
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        // Disabled guard: no relocate — source-confined, ResolvedDestinationRoot null, under the source folder.
        Assert.Null(item.ResolvedDestinationRoot);
        Assert.Equal("InPlace", item.MatchedRule);
        Assert.EndsWith("library/incoming/Sorted/My Film.mkv", item.NewFullPath);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task Excluded_ProducesSkipExcluded_ForEveryFile_NotSkipGated()
    {
        // An excluded multi-file entity yields a SkipExcluded skip-with-reason for EVERY file
        // (mirrors the gated path), carrying the matched exclude rule label — and it is NOT the
        // (gating) SkipGated status, guarding the relabel.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(
            VideoFile(1, "a.mkv", SrcRoot),
            VideoFile(2, "b.mkv", SrcRoot)) with
        { TagRefs = [(7, "anime")] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions([SrcRoot]);
        var lk = Lookups(excludeTags: new HashSet<int>([7]));

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, item =>
        {
            Assert.Equal(RenamerStatus.SkipExcluded, item.Status);
            Assert.NotEqual(RenamerStatus.SkipGated, item.Status);
            Assert.Contains("Exclude:Tag:anime", item.Reason);
        });
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task ExcludedAndGated_ReportsSkipExcluded_NotSkipGated()
    {
        // An item that is BOTH gated (unorganized, only-organized on, no unorganized destination) AND
        // matches an exclude rule is attributed to the exclude: excludes are evaluated before the
        // gate, so the preview/log shows the real reason (SkipExcluded) rather than the gate.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(VideoFile(1, "a.mkv", SrcRoot)) with { Organized = false, TagRefs = [(7, "anime")] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            AllowedRoots = [SrcRoot],
            OnlyOrganized = true,            // would gate the unorganized item …
            UnorganizedDestination = "",     // … and no unorganized route to fall through to.
        };
        var lk = Lookups(excludeTags: new HashSet<int>([7]));

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipExcluded, item.Status);
        Assert.NotEqual(RenamerStatus.SkipGated, item.Status);
        Assert.Contains("Exclude:Tag:anime", item.Reason);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task RoutedToSameFolder_SameName_IsNoOp_NotMove()
    {
        // The move-to-itself bug: with a destination configured, EVERY file is a "move" (isMove
        // true). If the route resolves the file back to the folder it already lives in AND the
        // rendered name equals its current basename, nothing changes on disk — it must be NoOp, not
        // a Move reported (and executed) as a rename to its own identical path. Here the default
        // root IS the file's source root, no subfolder, and the filename template reproduces the
        // current basename stem — so target full path == current full path.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(VideoFile(1, "My Film.mkv", SrcRoot)) with { StudioId = 999, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",     // renders "My Film" → "My Film.mkv" == current basename
            FolderTemplate = "",             // no subfolder …
            AllowedRoots = [SrcRoot],
            DefaultDestination = SrcRoot,    // … and the destination IS the file's own folder
            EnableDefaultRelocate = true,
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.NoOp, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task RoutedToSameFolder_SameName_IsNoOp_WhenTheHostSuppliedABackslashPath()
    {
        // Cove does not guarantee a forward-slash Folder.Path — its own tests build
        // `new Folder { Path = "C:\\library" }` — so on a Windows host ParentFolderPath arrives with
        // backslashes while the confinement gate returns its target forward-slashed. The no-op check
        // compares the two ORDINALLY, so a file already sitting at its routed destination read as a
        // Move to its own path. This seeds the raw host shape deliberately: every other test here
        // goes through Fwd(), which normalizes away the one input that reproduces it.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "asserts Windows backslash Folder.Path semantics");

        var rawSrcRoot = @"C:\library\incoming";
        var file = new RenamerFile(
            FileId: 1, Kind: RenamerFileKind.Video, Basename: "My Film.mkv", ParentFolderId: 5,
            ParentFolderPath: rawSrcRoot, Format: "mkv", Width: 1920, Height: 1080,
            Duration: 3600, VideoCodec: "h264", AudioCodec: "aac", FrameRate: 30);

        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(file) with { StudioId = 999, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "",
            AllowedRoots = [rawSrcRoot],
            DefaultDestination = rawSrcRoot,
            EnableDefaultRelocate = true,
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.NoOp, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task DefaultRelocateEnabled_RoutesToDefaultRoot()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 999, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            AllowedRoots = [SrcRoot, DefaultRoot],
            DefaultDestination = DefaultRoot,
            EnableDefaultRelocate = true,
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        // Flag ON: the same unmatched item now routes to the default root (proving the flag is the guard).
        Assert.Equal(DefaultRoot, item.ResolvedDestinationRoot);
        Assert.Equal("Default", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(DefaultRoot), item.TargetVolume);
        Assert.Empty(port.SaveCalls);
    }
}
