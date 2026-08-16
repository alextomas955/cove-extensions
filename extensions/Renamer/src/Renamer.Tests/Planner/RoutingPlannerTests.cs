using System.Text.RegularExpressions;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the resolver is wired into <c>RenamerPlanner.PlanAsync</c>: a matched entity produces a Move
/// whose <see cref="RenamerPlanItem.ResolvedDestinationRoot"/> / <see cref="RenamerPlanItem.MatchedRule"/> /
/// <see cref="RenamerPlanItem.TargetVolume"/> reflect the matched rule, and the destination is the
/// rule's own root plus its own relative template — nothing else joined to it. An entity no rule
/// matched takes the DEFAULT destination instead. PURE — no disk, no DB; every test asserts zero
/// <c>SaveAsync</c> calls.
/// </summary>
[Trait("Tier", "L0")]
public sealed class RoutingPlannerTests
{
    // OS-aware absolute roots (path-syntax valid on the current OS), mirroring PathConfinementTests.
    private static string SrcRoot => OperatingSystem.IsWindows() ? @"C:\library\incoming" : "/srv/library/incoming";
    private static string StudioRoot => OperatingSystem.IsWindows() ? @"D:\studios\acme" : "/mnt/studios/acme";
    private static string TagRoot => OperatingSystem.IsWindows() ? @"E:\by-tag\anime" : "/mnt/by-tag/anime";
    // The one tag id the fixture uses; the entity carries it as an (id, name) pair so a rule can
    // key on the id while the preview reason still renders "anime".
    private const int AnimeTagId = 11;

    // The folder holding StudioRoot: what Cove's list shows after the user broadens that library path
    // by one level, which is the shape a RENAMED library path arrives in.
    private static string StudioRootParent => OperatingSystem.IsWindows() ? @"D:\studios" : "/mnt/studios";

    private static string PathRoot => OperatingSystem.IsWindows() ? @"F:\by-source" : "/mnt/by-source";
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
            Performers: [new RenamerPerformer(1, "Bob", false, null)],
            TagRefs: [(AnimeTagId, "anime")], Files: files);

    // The DEFAULT template, which a matched rule replaces rather than adds to — non-empty here so a
    // rule silently falling through to it would change the destination and be caught rather than
    // measured as a pass.
    private static RenamerOptions MoveOptions(List<string> roots) =>
        new() { FilenameTemplate = "$title", FolderTemplate = "Sorted", AllowedRoots = roots };

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
            studio ?? RouteLookupsFixtures.RoutingNeutral.StudioIdToDest,
            tag ?? RouteLookupsFixtures.RoutingNeutral.TagIdToDest,
            exact ?? RouteLookupsFixtures.RoutingNeutral.PathExactToDest,
            regex ?? RouteLookupsFixtures.RoutingNeutral.PathRegexRules,
            excludeTags, excludeStudios, excludePathsExact, excludePathRegex);

    [Fact]
    public async Task StudioRouted_CarriesRootRuleAndVolume()
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(StudioRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions([SrcRoot, StudioRoot]);
        var lk = Lookups(studio: new Dictionary<int, Destination> { [42] = Dests.At(StudioRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(StudioRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Studio:42(direct)", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(StudioRoot), item.TargetVolume);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task StudioRouted_BareRootTemplate_LandsAtThatRoot_NotUnderTheSource()
    {
        // A rule whose template is a bare root drops the file at that root with no subfolder — the whole
        // path asserted, which the sibling tests above do not do. The move must land on the destination
        // volume's root, not silently rename in place under the source folder.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(StudioRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "",                 // no default subfolder — the rule alone drives the move
            AllowedRoots = [SrcRoot, StudioRoot],
        };
        var lk = Lookups(studio: new Dictionary<int, Destination> { [42] = Dests.At(StudioRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(StudioRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Studio:42(direct)", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(StudioRoot), item.TargetVolume);
        // The file lands at the ROOT of the routed destination (no subfolder), NOT under its source.
        Assert.Equal(Fwd(StudioRoot) + "/My Film.mkv", item.NewFullPath);
        Assert.DoesNotContain("incoming", item.NewFullPath);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task TagRouted_ById_CarriesRootAndRule()
    {
        var port = new FakeRenamerDataPort();
        // The rule is keyed on the tag id; the reason string still renders the tag's name.
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { TagRefs = [(AnimeTagId, "anime")] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions([SrcRoot, TagRoot]);
        port.SeedLibraryRoot(TagRoot);
        var lk = Lookups(tag: new Dictionary<int, Destination> { [AnimeTagId] = Dests.At(TagRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(TagRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Tag:anime", item.MatchedRule);
        Assert.Equal(Path.GetPathRoot(TagRoot), item.TargetVolume);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task SourcePathRouted_Exact_CarriesRootAndRule()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions([SrcRoot, PathRoot]);
        port.SeedLibraryRoot(PathRoot);
        var lk = Lookups(
            exact: new Dictionary<string, Destination>(StringComparer.Ordinal) { [Fwd(SrcRoot)] = Dests.At(PathRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(PathRoot), item.ResolvedDestinationRoot);
        Assert.Equal("SourcePath:exact", item.MatchedRule);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task UnorganizedRouted_ProducesMove_NotSkip()
    {
        var port = new FakeRenamerDataPort();
        // Organized=false + an UnorganizedDestination set → routes to it, does not gate to a skip.
        port.SeedLibraryRoot(UnorgRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { Organized = false, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            AllowedRoots = [SrcRoot, UnorgRoot],
            UnorganizedDestination = Dests.At(UnorgRoot),
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(UnorgRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Unorganized", item.MatchedRule);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task NoRuleMatched_TheDefaultTemplateRenders_AnchoredOnTheLibraryPath()
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(SrcRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        // Empty lookups + empty maps + no allowed roots → no rule matched, so the DEFAULT template
        // renders, anchored on the CONTAINING Cove library path, and MatchedPathTemplate stays null.
        var opts = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "Sorted" };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        // No rule matched, so the default destination measured from the library path holding the file.
        Assert.Equal(Fwd(SrcRoot), item.ResolvedDestinationRoot);
        Assert.Equal("Default", item.MatchedRule);
        // The file sits AT its library path here, so the rendered folder lands one level below it —
        // and, unlike the parent-relative arrangement this replaced, lands in the same place on a
        // second run.
        Assert.EndsWith("library/incoming/Sorted/My Film.mkv", item.NewFullPath);
        // An anchored item has no destination volume of interest, so TargetVolume is empty — never the
        // fictitious synthetic-anchor root.
        Assert.Equal("", item.TargetVolume);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task AMatchedRule_RendersOnlyItsOwnTemplate_TheDefaultIsNotAppended()
    {
        // The crux of one-lookup-one-template, pinned where routing is decided, and the shape that
        // produced a library path repeated inside itself on a real library: a rule's destination and the
        // default template BOTH answered "where does this file go", and joining the two appended the
        // default's own path to the rule's. Here the two name the same tree — the case that made the
        // repetition visible — and the destination must be the rule's template alone.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(SrcRoot);
        port.SeedLibraryRoot(StudioRoot);
        port.SeedEntity(Entity(VideoFile(1, "raw.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            // Spelled to overlap the rule deliberately: appended, this used to produce
            // "D:/studios/acme/videos/videos".
            FolderTemplate = "videos",
            FolderRoot = StudioRoot,
        };
        var lk = Lookups(studio: new Dictionary<int, Destination> { [42] = Dests.At(StudioRoot, "videos") });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(StudioRoot) + "/videos/My Film.mkv", item.NewFullPath);
        Assert.Equal(Fwd(StudioRoot), item.ResolvedDestinationRoot);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    // ── A rule's chosen root, re-checked against Cove's library paths on every plan ────────────────
    //
    // The first pair below is one boundary and is written as one. Either half alone passes against code
    // that is wrong in the other direction: the skip case alone passes against a planner that skips
    // every routed item, and the present-root case alone passes against one that never checks. The two
    // fixtures differ ONLY in whether the chosen root was declared a library path.
    //
    // Those two also answer what happens when the user ADDS a library path: the list already holds a
    // path that is not the rule's — SrcRoot, the one holding the file, which is the strongest candidate
    // for a planner that re-anchored on something other than the rule's own answer — and the rule still
    // decides. For a destination set to "(the file's own library path)" the answer is the innermost
    // containing path, pinned at PathConfinementTests.ContainingRoot_WhenLibraryPathsNest_TheLongestWins.

    [Fact]
    public async Task ARulesChosenRoot_NoLongerALibraryPath_SkipsAndNamesTheRule()
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(SrcRoot);   // …but NOT StudioRoot, which the rule chose.
        port.SeedEntity(Entity(
            VideoFile(1, "a.mkv", SrcRoot),
            VideoFile(2, "b.mkv", SrcRoot)) with
        { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title" };
        var lk = Lookups(studio: new Dictionary<int, Destination> { [42] = Dests.At(StudioRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        // EVERY file of the item, so a multi-file entity is not half-moved.
        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, item =>
        {
            Assert.Equal(RenamerStatus.SkipRootMissing, item.Status);
            Assert.Equal(item.OldFullPath, item.NewFullPath);
            Assert.Contains(StudioRoot, item.Reason);
            Assert.Contains("Studio:42(direct)", item.Reason);
        });
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task ARulesChosenRoot_StillALibraryPath_DoesNotSkip()
    {
        // The negative side: the SAME rule, the same options, the one difference being that the chosen
        // root is still declared. It must move — never fall through to the default, and never skip.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(SrcRoot);
        port.SeedLibraryRoot(StudioRoot);
        port.SeedEntity(Entity(VideoFile(1, "a.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title" };
        var lk = Lookups(studio: new Dictionary<int, Destination> { [42] = Dests.At(StudioRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(Fwd(StudioRoot) + "/My Film.mkv", item.NewFullPath);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task ARulesChosenRoot_NowMerelyINSIDEALibraryPath_SkipsLikeAnyOtherMissingRoot()
    {
        // Renaming a library path in Cove is not an event Renamer can follow: it is handed the CURRENT
        // list, never a history of edits, so a rename reads as one path removed and another added. The
        // hardest form of that is a rename which leaves the old root still INSIDE the new one — broaden
        // 'D:/studios/acme' to 'D:/studios' and every stored root is still contained by a library path.
        // The check is MEMBERSHIP of the list, so the rule still skips and the user re-picks; a
        // containment test would keep writing into a folder Cove no longer declares, and would do it
        // silently. This is the sole pin on that direction — the removal case above uses an unrelated
        // root, which a containment test refuses too.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(SrcRoot);
        port.SeedLibraryRoot(StudioRootParent);   // the rule's root is now one level below a library path
        port.SeedEntity(Entity(VideoFile(1, "a.mkv", SrcRoot)) with { StudioId = 42, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title" };
        var lk = Lookups(studio: new Dictionary<int, Destination> { [42] = Dests.At(StudioRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipRootMissing, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
        Assert.Contains("Studio:42(direct)", item.Reason);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task TheDefaultDestinationsChosenRoot_NoLongerALibraryPath_SkipsToo()
    {
        // The default is a destination like any other, so the same re-check applies to it. Named
        // separately because its label is "Default" rather than a rule's, and a user reading the dry run
        // has to be told WHICH destination broke.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(SrcRoot);
        port.SeedEntity(Entity(VideoFile(1, "a.mkv", SrcRoot)) with { TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            FolderRoot = UnorgRoot,   // never declared a library path
        };

        var plan = await planner.PlanAsync(
            RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipRootMissing, item.Status);
        Assert.Contains("Default", item.Reason);
        Assert.Empty(port.ApplyAndSaveCalls);
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
        { TagRefs = [(AnimeTagId, "anime")] });
        var planner = new RenamerPlanner(port);
        var opts = MoveOptions([SrcRoot]);
        var lk = Lookups(excludeTags: new HashSet<int> { AnimeTagId });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, item =>
        {
            Assert.Equal(RenamerStatus.SkipExcluded, item.Status);
            Assert.NotEqual(RenamerStatus.SkipGated, item.Status);
            Assert.Contains("Exclude:Tag:anime", item.Reason);
        });
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task ExcludedAndGated_ReportsSkipExcluded_NotSkipGated()
    {
        // An item that is BOTH gated (unorganized, only-organized on, no unorganized destination) AND
        // matches an exclude rule is attributed to the exclude: excludes are evaluated before the
        // gate, so the preview/log shows the real reason (SkipExcluded) rather than the gate.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(VideoFile(1, "a.mkv", SrcRoot)) with { Organized = false, TagRefs = [(AnimeTagId, "anime")] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            AllowedRoots = [SrcRoot],
            OnlyOrganized = true,            // would gate the unorganized item …
            UnorganizedDestination = null,   // … and no unorganized route to fall through to.
        };
        var lk = Lookups(excludeTags: new HashSet<int> { AnimeTagId });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipExcluded, item.Status);
        Assert.NotEqual(RenamerStatus.SkipGated, item.Status);
        Assert.Contains("Exclude:Tag:anime", item.Reason);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task RoutedToSameFolder_SameName_IsNoOp_NotMove()
    {
        // The move-to-itself bug: with a destination configured, EVERY file is a "move" (isMove
        // true). If the route resolves the file back to the folder it already lives in AND the
        // rendered name equals its current basename, nothing changes on disk — it must be NoOp, not
        // a Move reported (and executed) as a rename to its own identical path. Here a source-path
        // rule routes the file to its OWN root, no subfolder, and the filename template reproduces the
        // current basename stem — so target full path == current full path.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(SrcRoot);
        port.SeedEntity(Entity(VideoFile(1, "My Film.mkv", SrcRoot)) with { StudioId = 999, TagRefs = [] });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",     // renders "My Film" → "My Film.mkv" == current basename
            FolderTemplate = "",             // no subfolder …
            AllowedRoots = [SrcRoot],
        };

        // … and the routed destination IS the file's own folder.
        var lk = Lookups(
            exact: new Dictionary<string, Destination>(StringComparer.Ordinal) { [Fwd(SrcRoot)] = Dests.At(SrcRoot) });

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, lk, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.NoOp, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
        Assert.Empty(port.ApplyAndSaveCalls);
    }
}
