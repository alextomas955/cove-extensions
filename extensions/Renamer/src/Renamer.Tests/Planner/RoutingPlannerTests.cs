using System.Text.RegularExpressions;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the resolver is wired into <c>RenamerPlanner.PlanAsync</c>: a routed entity produces
/// a Move whose <see cref="RenamerPlanItem.ResolvedDestinationRoot"/> / <see cref="RenamerPlanItem.MatchedRule"/>
/// / <see cref="RenamerPlanItem.TargetVolume"/> reflect the matched route, and confinement is anchored
/// on the destination's own root (so the move lands on the destination volume). An entity no rule
/// matched takes the DEFAULT destination, measured from the library path holding the file. PURE - no
/// disk, no DB; every test asserts zero <c>SaveAsync</c> calls.
/// </summary>
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

    /// <summary>A port whose library paths are <paramref name="libraryPaths"/> - the roots a destination may name.</summary>
    private static FakeRenamerDataPort Port(params string[] libraryPaths)
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryPaths(libraryPaths);
        return port;
    }

    // A move-producing render: a non-empty folder template makes isMove true, so the destination root is
    // the confinement anchor and the absolute target lands on the destination volume.
    private static RenamerOptions MoveOptions() =>
        new() { FilenameTemplate = "$title", FolderTemplate = "Sorted" };

    private static RouteLookups Lookups(
        IReadOnlyDictionary<int, Destination>? studio = null,
        IReadOnlyDictionary<int, Destination>? tag = null,
        IReadOnlyDictionary<string, Destination>? exact = null,
        IReadOnlyList<(Regex, Destination)>? regex = null,
        IReadOnlySet<int>? excludeTags = null,
        IReadOnlySet<int>? excludeStudios = null,
        IReadOnlySet<string>? excludePathsExact = null,
        IReadOnlyList<Regex>? excludePathRegex = null) =>
        new(
            studio ?? new Dictionary<int, Destination>(),
            tag ?? new Dictionary<int, Destination>(),
            exact ?? new Dictionary<string, Destination>(StringComparer.Ordinal),
            regex ?? Array.Empty<(Regex, Destination)>(),
            excludeTags, excludeStudios, excludePathsExact, excludePathRegex);

    [Fact]
    public async Task StudioRouted_CarriesRootRuleAndVolume()
    {
        var port = Port(SrcRoot, StudioRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions();
        var lk = Lookups(studio: new Dictionary<int, Destination> { [42] = Dest.At(StudioRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(StudioRoot), item.ResolvedDestinationRoot);
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
        var port = Port(SrcRoot, StudioRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
        };
        var lk = Lookups(studio: new Dictionary<int, Destination> { [42] = Dest.At(StudioRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(StudioRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Studio:42(direct)", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(StudioRoot), item.TargetVolume);
        // The file lands at the ROOT of the routed destination (no subfolder), NOT under its source.
        Assert.Equal(Fwd(StudioRoot) + "/My Film.mkv", item.NewFullPath);
        Assert.DoesNotContain("incoming", item.NewFullPath);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task RoutedRule_RendersItsOwnTemplate_NotTheDefaultOne()
    {
        // The rule's template REPLACES the default rather than being appended to it: the two are never
        // joined, which is what keeps a plan a fixed point under the move it names.
        var port = Port(SrcRoot, StudioRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "Default" };
        var lk = Lookups(studio: new Dictionary<int, Destination>
        {
            [42] = Dest.At(StudioRoot, "$studio"),
        });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(Fwd(StudioRoot) + "/Acme/My Film.mkv", item.NewFullPath);
        Assert.DoesNotContain("Default", item.NewFullPath);
    }

    [Fact]
    public async Task TagRouted_MatchesTheRenamedTag_AndReportsItsCurrentName()
    {
        var port = Port(SrcRoot, TagRoot);
        // The rule was written when tag 7 was called something else. It routes on the id, so the
        // rename cannot break it, and the reason shows the name the tag carries NOW.
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { TagRefs = [(7, "Anime (renamed)")] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions();
        var lk = Lookups(tag: new Dictionary<int, Destination> { [7] = Dest.At(TagRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(TagRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Tag:Anime (renamed)", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(TagRoot), item.TargetVolume);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task SourcePathRouted_Exact_CarriesRootAndRule()
    {
        var port = Port(SrcRoot, PathRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions();
        var lk = Lookups(exact: new Dictionary<string, Destination>(StringComparer.Ordinal)
        {
            [Fwd(SrcRoot)] = Dest.At(PathRoot),
        });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(PathRoot), item.ResolvedDestinationRoot);
        Assert.Equal("SourcePath:exact", item.MatchedRule);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task UnorganizedRouted_ProducesMove_NotSkip()
    {
        var port = Port(SrcRoot, UnorgRoot);
        // Organized=false + an UnorganizedDestination set → routes to it, does not gate to a skip.
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { Organized = false, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            UnorganizedDestination = Dest.At(UnorgRoot, "Sorted"),
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(UnorgRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Unorganized", item.MatchedRule);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task Unmatched_MeasuresTheDefaultTemplateFromTheContainingLibraryPath()
    {
        var port = Port(SrcRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot + "/Sorted")) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        // The default destination names no root, so it measures from the library path holding the file.
        var opts = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "Sorted" };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal("Default", item.MatchedRule);
        // The rendered folder lands under the LIBRARY path, not under the file's own parent - which is
        // the previous run's output, and re-anchoring on it descends one directory per pass.
        Assert.Equal(Fwd(SrcRoot) + "/Sorted/My Film.mkv", item.NewFullPath);
        Assert.Equal(Fwd(SrcRoot), item.ResolvedDestinationRoot);
        // An item measured from its own library path stays on the volume it is already on, so it has no
        // destination volume of interest.
        Assert.Equal("", item.TargetVolume);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task Unmatched_SecondPass_IsANoOp_NotAnotherDescent()
    {
        // The fixed point the library anchor buys: the file already sits where the first pass put it,
        // so the second pass computes the SAME path and changes nothing.
        var port = Port(SrcRoot);
        port.SeedEntity(Entity(VideoFile(1, "My Film.mkv", SrcRoot + "/Sorted")) with { TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "Sorted" };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.NoOp, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task Unmatched_FileUnderNoLibraryPath_IsSkipUnanchored()
    {
        // A destination measuring from the file's own library path, and the file is under none: the
        // destination is not forbidden, it cannot be computed. The item keeps its name AND its folder.
        var port = Port(StudioRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "Sorted" };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipUnanchored, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
        Assert.Equal(Fwd(SrcRoot), item.TargetFolderPath);
        Assert.Contains("under none", item.Reason);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task Unmatched_NoRootAndNoTemplate_MovesNothing()
    {
        // Both halves of the default destination empty is the state that relocates nothing, so a file
        // under no library path at all is still renamed in place rather than skipped.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title" };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Renamer, item.Status);
        Assert.Equal(Fwd(SrcRoot) + "/My Film.mkv", item.NewFullPath);
        Assert.Null(item.ResolvedDestinationRoot);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task ChosenRootNoLongerALibraryPath_IsSkipRootMissing_ForEveryFile()
    {
        // The user removed that folder from Cove's library paths. The rule cannot be honoured, and
        // handing its items to the default instead would relocate them somewhere nobody chose.
        var port = Port(SrcRoot);
        port.SeedEntity(Entity(
            VideoFile(1, "a.mkv", SrcRoot),
            VideoFile(2, "b.mkv", SrcRoot)) with
        { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions();
        var lk = Lookups(studio: new Dictionary<int, Destination> { [42] = Dest.At(StudioRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, item =>
        {
            Assert.Equal(RenamerStatus.SkipRootMissing, item.Status);
            Assert.Equal(item.OldFullPath, item.NewFullPath);
            Assert.Contains("Studio:42(direct)", item.Reason);
        });
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task Excluded_ProducesSkipExcluded_ForEveryFile_NotSkipGated()
    {
        // An excluded multi-file entity yields a SkipExcluded skip-with-reason for EVERY file
        // (mirrors the gated path), carrying the matched exclude rule label — and it is NOT the
        // (gating) SkipGated status, guarding the relabel.
        var port = Port(SrcRoot);
        port.SeedEntity(Entity(
            VideoFile(1, "a.mkv", SrcRoot),
            VideoFile(2, "b.mkv", SrcRoot)) with
        { TagRefs = [(7, "anime")] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions();
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
        var port = Port(SrcRoot);
        port.SeedEntity(Entity(VideoFile(1, "a.mkv", SrcRoot)) with { Organized = false, TagRefs = [(7, "anime")] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            OnlyOrganized = true,            // would gate the unorganized item …
            // … and no unorganized route to fall through to (the absent member is how that is spelled).
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
        var port = Port(SrcRoot);
        port.SeedEntity(Entity(VideoFile(1, "My Film.mkv", SrcRoot)) with { StudioId = 999, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",     // renders "My Film" → "My Film.mkv" == current basename
            FolderRoot = SrcRoot,            // … and the destination IS the file's own folder
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

        var port = Port(rawSrcRoot);
        port.SeedEntity(Entity(file) with { StudioId = 999, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderRoot = rawSrcRoot,
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.NoOp, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task DefaultDestination_WithAChosenRoot_RelocatesTheUnmatchedItem()
    {
        var port = Port(SrcRoot, DefaultRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 999, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            FolderRoot = DefaultRoot,
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(DefaultRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Default", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(DefaultRoot), item.TargetVolume);
        Assert.Empty(port.SaveCalls);
    }
}
