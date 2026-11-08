using System.Text.RegularExpressions;
using Renamer.Engine;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

public sealed class RoutingPlannerTests
{
    private static string SrcRoot => OperatingSystem.IsWindows() ? @"C:\library\incoming" : "/srv/library/incoming";
    private static string StudioRoot => OperatingSystem.IsWindows() ? @"D:\studios\acme" : "/mnt/studios/acme";
    private static string TagRoot => OperatingSystem.IsWindows() ? @"E:\by-tag\anime" : "/mnt/by-tag/anime";
    private static string PathRoot => OperatingSystem.IsWindows() ? @"F:\by-source" : "/mnt/by-source";
    private static string DefaultRoot => OperatingSystem.IsWindows() ? @"G:\overflow" : "/mnt/overflow";

    private static string Fwd(string p) => p.Replace('\\', '/');

    private static RenamerFile VideoFile(int id, string basename, string folderPath) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: 5,
            ParentFolderPath: Fwd(folderPath), Format: "mkv", Width: 1920, Height: 1080,
            Duration: 3600, VideoCodec: "h264", AudioCodec: "aac", FrameRate: 30);

    private static RenamerEntity Entity(
        params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: "ABC-1", StudioName: "Acme",
            Date: new DateOnly(2024, 3, 2), Organized: true,
            Performers: [new RenamerPerformer(1, "Bob", null)], TagRefs: [(7, "anime")], Files: files);

    // Library paths are the roots a destination may name.
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

    [Theory]
    [InlineData(Spelling.BackslashSeparators)]
    [InlineData(Spelling.TrailingSeparator)]
    public async Task ARootStoredInAnotherSpelling_StillNamesItsLibraryPath(Spelling spelling)
    {
        if (spelling == Spelling.BackslashSeparators)
        {
            // A backslash names a directory boundary only on Windows. Elsewhere it is an ordinary
            // character in a folder name, and folding it would be wrong rather than lenient.
            Assert.SkipUnless(OperatingSystem.IsWindows(), "asserts Windows path separator semantics");
        }

        string stored = spelling switch
        {
            Spelling.BackslashSeparators => @"D:\studios\acme",
            Spelling.TrailingSeparator => Fwd(StudioRoot) + "/",
            _ => throw new ArgumentOutOfRangeException(nameof(spelling)),
        };

        var port = Port(SrcRoot, StudioRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var lk = Lookups(studio: new Dictionary<int, Destination> { [42] = Dest.At(stored) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, MoveOptions(), lk, default);

        var item = Assert.Single(plan.Items);

        // A spelling the membership comparison did not fold would surface as SkipRootMissing - the
        // rule silently ceasing to route - not as a different path.
        Assert.Equal(RenamerStatus.Move, item.Status);

        // Compared with any trailing separator trimmed, because one is carried through into the
        // reported root rather than normalised away: a rule stored as "<root>/" reports "<root>/"
        // here while a rule stored as "<root>" reports "<root>". Both route to the same place, so
        // this is a cosmetic inconsistency in a projection field and not a routing defect.
        Assert.Equal(Fwd(StudioRoot), item.ResolvedDestinationRoot!.TrimEnd('/'));
    }

    [Fact]
    public async Task ASourcePathRule_ThenAConfiguredDefault_RelocatesTwice_AndStillReachesNoOp()
    {
        const int MaxPasses = 8;

        var port = Port(SrcRoot, StudioRoot);
        var planner = new RenamerPlanner(port);

        var options = new RenamerOptions
        {
            FilenameTemplate = "$title",
            // The one thing that makes this differ from the rule acting alone: a default that moves.
            FolderRoot = SrcRoot,
            FolderTemplate = "Sorted",
        };

        var lk = Lookups(exact: new Dictionary<string, Destination>(StringComparer.Ordinal)
        {
            [Fwd(SrcRoot)] = Dest.At(StudioRoot, "Sorted"),
        });

        var entity = Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] };
        var trace = new List<string>();
        string? firstMatchedRule = null;
        bool settled = false;

        for (int pass = 1; pass <= MaxPasses && !settled; pass++)
        {
            port.SeedEntity(entity);
            var items = (await planner.PlanAsync(entity.Kind, entity.EntityId, options, lk, default)).Items;

            if (items.All(i => i.Status == RenamerStatus.NoOp))
            {
                settled = true;
                break;
            }

            var blocked = items.FirstOrDefault(
                i => i.Status is not (RenamerStatus.Rename or RenamerStatus.Move or RenamerStatus.NoOp));
            Assert.True(blocked is null, $"pass {pass} was blocked as {blocked?.Status}: {blocked?.Reason}");

            firstMatchedRule ??= items[0].MatchedRule;
            trace.AddRange(items.Where(i => i.Status != RenamerStatus.NoOp).Select(i => i.NewFullPath));

            // The executor's whole effect on the next plan's input.
            entity = entity with
            {
                Files = [.. entity.Files.Select(f =>
                {
                    var planned = items.First(i => i.FileId == f.FileId);
                    return f with { Basename = planned.NewBasename, ParentFolderPath = planned.TargetFolderPath };
                })],
            };
        }

        Assert.True(settled, $"never reached a fixed point in {MaxPasses} passes: {string.Join(" | ", trace)}");
        Assert.Equal("SourcePath:exact", firstMatchedRule);
        Assert.Equal(2, trace.Count);
    }

    // How a stored destination root spells the library path it names.
    public enum Spelling
    {
        // Backslash separators, as a store written on a Windows host holds them.
        BackslashSeparators,

        TrailingSeparator,
    }

    // The route's template replaces the default rather than being appended to it, so an empty route
    // template erases a non-empty default and the file lands at the root of the routed destination.
    // A matched route relocates the file either way, rather than renaming it in place under its
    // source folder.
    [Theory]
    [InlineData("Sorted", "", "/My Film.mkv")]
    [InlineData("", "", "/My Film.mkv")]
    [InlineData("Default", "$studio", "/Acme/My Film.mkv")]
    public async Task StudioRouted_RendersTheRoutesOwnTemplate_UnderTheRoutedRoot(
        string defaultFolderTemplate, string routeTemplate, string expectedPathUnderRoot)
    {
        var port = Port(SrcRoot, StudioRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = defaultFolderTemplate,
        };
        var lk = Lookups(studio: new Dictionary<int, Destination>
        {
            [42] = Dest.At(StudioRoot, routeTemplate),
        });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(StudioRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Studio:42(direct)", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(StudioRoot), item.TargetVolume);
        Assert.Equal(Fwd(StudioRoot) + expectedPathUnderRoot, item.NewFullPath);
    }

    [Fact]
    public async Task TagRouted_MatchesTheRenamedTag_AndReportsItsCurrentName()
    {
        var port = Port(SrcRoot, TagRoot);
        // The rule was written when tag 7 was called something else. It routes on the id, so the
        // rename cannot break it, and the reason shows the name the tag carries now.
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
        // The rendered folder lands under the library path, not under the file's own parent - which is
        // the previous run's output, and re-anchoring on it descends one directory per pass.
        Assert.Equal(Fwd(SrcRoot) + "/Sorted/My Film.mkv", item.NewFullPath);
        Assert.Equal(Fwd(SrcRoot), item.ResolvedDestinationRoot);
        // An item measured from its own library path stays on the volume it is already on, so it has no
        // destination volume of interest.
        Assert.Equal("", item.TargetVolume);
    }

    [Fact]
    public async Task Unmatched_SecondPass_IsANoOp_NotAnotherDescent()
    {
        var port = Port(SrcRoot);
        port.SeedEntity(Entity(VideoFile(1, "My Film.mkv", SrcRoot + "/Sorted")) with { TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "Sorted" };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.NoOp, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
    }

    [Fact]
    public async Task Unmatched_FileUnderNoLibraryPath_IsSkipUnanchored()
    {
        // A destination measuring from the file's own library path, and the file is under none: the
        // destination is not forbidden, it cannot be computed.
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
        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.Equal(Fwd(SrcRoot) + "/My Film.mkv", item.NewFullPath);
        Assert.Null(item.ResolvedDestinationRoot);
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
    }

    [Fact]
    public async Task Excluded_ProducesSkipExcluded_ForEveryFile_NotSkipGated()
    {
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
            Assert.Contains("Exclude:Tag:anime", item.Reason);
        });
    }

    [Fact]
    public async Task ATimedOutExcludeRule_SkipsEveryFile_AndNamesThePattern()
    {
        var port = Port(SrcRoot);
        port.SeedEntity(Entity(
            VideoFile(1, "a.mkv", SrcRoot),
            VideoFile(2, "b.mkv", SrcRoot)));
        var planner = new RenamerPlanner(port);
        var redos = new Regex("^(.+)+#$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var lk = Lookups(excludePathRegex: [redos]);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, MoveOptions(), lk, default);

        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, item =>
        {
            Assert.Equal(RenamerStatus.SkipRuleTimedOut, item.Status);
            Assert.Equal(item.OldFullPath, item.NewFullPath);
            Assert.Contains("Exclude:Path:regex:^(.+)+#$", item.Reason);
        });
    }

    [Fact]
    public async Task ExcludedAndGated_ReportsSkipExcluded_NotSkipGated()
    {
        // An item that is both gated (unorganized, only-organized on, no unorganized destination) and
        // matches an exclude rule is attributed to the exclude: excludes are evaluated before the
        // gate, so the preview/log shows the real reason (SkipExcluded) rather than the gate.
        var port = Port(SrcRoot);
        port.SeedEntity(Entity(VideoFile(1, "a.mkv", SrcRoot)) with { Organized = false, TagRefs = [(7, "anime")] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",

            // Gates the unorganized item, with no unorganized route to fall through to - the absent
            // member is how that is spelled.
            OnlyOrganized = true,
        };
        var lk = Lookups(excludeTags: new HashSet<int>([7]));

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipExcluded, item.Status);
        Assert.Contains("Exclude:Tag:anime", item.Reason);
    }

    [Fact]
    public async Task RoutedToSameFolder_SameName_IsNoOp_NotMove()
    {
        // With a destination configured every file is a move, so a route that resolves the file back
        // to the folder it already lives in under its current name has to settle as NoOp rather than
        // a Move executed as a rename to its own path.
        var port = Port(SrcRoot);
        port.SeedEntity(Entity(VideoFile(1, "My Film.mkv", SrcRoot)) with { StudioId = 999, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",     // renders the current basename, "My Film.mkv"
            FolderRoot = SrcRoot,            // and the destination is the file's own folder
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, Lookups(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.NoOp, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
    }

    [Fact]
    public async Task RoutedToSameFolder_SameName_IsNoOp_WhenTheHostSuppliedABackslashPath()
    {
        // Cove does not guarantee a forward-slash Folder.Path - its own tests build
        // `new Folder { Path = "C:\\library" }` - so on a Windows host ParentFolderPath arrives with
        // backslashes while the confinement gate returns its target forward-slashed, and the no-op
        // check compares the two ordinally. This seeds the raw host shape deliberately: every other
        // test here goes through Fwd(), which normalizes away the one input that reaches the case.
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
    }
}
