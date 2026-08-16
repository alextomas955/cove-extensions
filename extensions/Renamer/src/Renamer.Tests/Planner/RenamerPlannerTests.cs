using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Dry-run core: <c>RenamerPlanner.PlanAsync</c> produces an accurate per-file
/// old→new plan with the right <see cref="RenamerStatus"/> while mutating NOTHING — every test
/// asserts the <see cref="FakeRenamerDataPort"/> recorded zero <c>SaveAsync</c> calls. Also covers
/// the happy-path renamer, NoOp, and the confinement rejection; the one-item-per-file rule for a
/// multi-file entity; the two advisory badges (<c>Suffixed</c>, <c>Sanitized</c>) the planner sets on
/// the final item; and PHASE A's load de-duplication, where N planned ids must cost exactly N loads.
/// </summary>
[Trait("Tier", "L0")]
public sealed class RenamerPlannerTests
{
    private static RenamerFile VideoFile(int id, string basename, int folderId = 5, string folderPath = "media/videos") =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: folderId,
            ParentFolderPath: folderPath, Format: "mkv", Width: 1920, Height: 1080,
            Duration: 3600, VideoCodec: "h264", AudioCodec: "aac", FrameRate: 30);

    private static RenamerEntity VideoEntity(string title, params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: title, Code: "ABC-1", StudioName: "Acme",
            Date: new DateOnly(2024, 3, 2), Organized: true,
            Performers: [new RenamerPerformer(1, "Bob", false, null)], TagRefs: [(7, "hd")], Files: files);

    // Deliberately metadata-FREE, and not interchangeable with VideoFile/VideoEntity above: the default
    // FilenameTemplate is "{$date - }$title{ [$resolution]}", so a file carrying Width/Height and an
    // entity carrying a Date render extra tokens. Every case below that plans under the DEFAULT template
    // depends on both optional groups collapsing to nothing, which is what these two shapes guarantee.
    private static RenamerFile PlainFile(int id, string basename, int folderId = 5, long sizeBytes = 0) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: folderId,
            ParentFolderPath: "media/videos", Format: "mkv", SizeBytes: sizeBytes);

    private static RenamerEntity PlainEntity(string title, params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: title, Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [], Files: files);

    [Fact]
    public async Task SingleFile_Renamer_HappyPath_ZeroMutation()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        // Pin the title-only template (this test exercises planner renamer detection, not the default).
        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions { FilenameTemplate = "$title" }, RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.Equal("My Film.mkv", item.NewBasename);
        Assert.EndsWith("My Film.mkv", item.NewFullPath);
        Assert.EndsWith("media/videos/raw.mkv", item.OldFullPath);
        Assert.Empty(port.ApplyAndSaveCalls);               // dry-run guarantee: no mutation
    }

    [Fact]
    public async Task TagWhitelistIds_FilterTheRenderedTagsToken_ProvingThePlannerPassesTagRecords()
    {
        // The tag-records side input is an OPTIONAL parameter, so a planner that stopped passing it
        // would still compile and would silently render every tag. This is the assertion that fails
        // in that case — it is the only place the production call site itself is exercised.
        var entity = VideoEntity("My Film", VideoFile(1, "raw.mkv"));
        var port = new FakeRenamerDataPort();
        port.SeedEntity(entity with { TagRefs = [(7, "hd"), (9, "fav")] });
        var planner = new RenamerPlanner(port);

        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title $tags",
            Tags = new MultiValueOptions { Separator = " ", Sort = SortOrder.None, WhitelistIds = [9] },
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default);

        // Only tag id 9 survives the whitelist, and it renders as its name, not its number.
        Assert.Equal("My Film fav.mkv", Assert.Single(plan.Items).NewBasename);
    }

    [Fact]
    public async Task RenderedEqualsCurrent_IsNoOp_ZeroMutation()
    {
        var port = new FakeRenamerDataPort();
        // Template "$title" with Title="raw" renders to "raw.mkv" == current basename → NoOp.
        port.SeedEntity(VideoEntity("raw", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions { FilenameTemplate = "$title" }, RouteLookupsFixtures.RoutingNeutral, default);

        Assert.Equal(RenamerStatus.NoOp, Assert.Single(plan.Items).Status);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task TraversalFolderTemplate_NeutralizedByEngine_ConfinedUnderRoot()
    {
        // Defense-in-depth: the engine strips "../" segments per-segment (TrimEdge dots),
        // so "../../escape" renders to the benign subfolder "escape" — which the confinement gate
        // then ACCEPTS as a move UNDER the root. The raw "../.." → rejected path is proven directly
        // at the helper level in PathConfinementTests.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot("media/videos");
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);
        // Pin the title-only filename template — this test asserts folder-template confinement, not the default name shape.
        var opts = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "../../escape" };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.EndsWith("media/videos/escape/My Film.mkv", item.NewFullPath);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task ConfinementRejection_WiredIntoPlanner_IsSkipped_ZeroMutation()
    {
        // Drive a real confinement REJECTION through the planner via the FullPathMax re-check
        // the engine never measures on the absolute path — proves the planner classifies
        // a confinement failure as a skip with the helper's reason, mutating nothing.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(VideoEntity(new string('A', 300), VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FullPathMax = 50 };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipTooLong, item.Status);
        Assert.Contains("FullPathMax", item.Reason);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task PermissionRefusalAndLengthRefusal_CarryDifferentStatuses_NotOneSharedName()
    {
        // The two refusals used to arrive as one status, badged "name conflict" — which is neither of
        // them, and which asks the user to wait for a clash to clear when what they must actually do is
        // widen a permission or shorten a template. Asserted as a PAIR, in one case, because the claim
        // is that they DIFFER: two separate cases each pinning one status would both still pass if the
        // planner collapsed them onto whichever status they happened to name.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot("media/videos");
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        // Same rendered destination both times. Only the constraint it offends changes.
        var refusedByPermission = await planner.PlanAsync(
            RenamerFileKind.Video, 10,
            new RenamerOptions
            {
                FilenameTemplate = "$title",
                FolderTemplate = "Archive",
                AllowedRoots = ["media/somewhere-else"],
            },
            RouteLookupsFixtures.RoutingNeutral, default);
        var refusedByLength = await planner.PlanAsync(
            RenamerFileKind.Video, 10,
            new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "Archive", FullPathMax = 10 },
            RouteLookupsFixtures.RoutingNeutral, default);

        Assert.Equal(RenamerStatus.SkipNotAllowed, Assert.Single(refusedByPermission.Items).Status);
        Assert.Equal(RenamerStatus.SkipTooLong, Assert.Single(refusedByLength.Items).Status);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task AnAllowlistNarrowerThanTheLibraryPath_IsRefusedNamingTheAnchor_NotOnlyTheAllowlist()
    {
        // The anchor moved in this release: an unrouted folder template is now placed under the Cove
        // library path holding the file rather than under the file's own folder. So an AllowedRoots entry
        // drawn at the file's own folder — which permitted this move before — now refuses it, and the
        // gate's own message ("not under any allowed root") sends the user to look at a list that already
        // contains the folder they are staring at. The reason has to name the anchor or the user cannot
        // act on it.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot("media");
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Archive",
            AllowedRoots = ["media/videos"],       // the file's own folder: narrower than the library path
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipNotAllowed, item.Status);
        Assert.Contains("Cove library path", item.Reason);
        Assert.Contains("'media'", item.Reason);   // the anchor itself, not merely the word for it
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    // Absolute both here and in production: the library paths Cove hands over are absolute, and the
    // containment compare is a string prefix rather than a resolve, so a relative fixture would measure
    // a shape the product never sees.
    private static string LibraryPath => OperatingSystem.IsWindows() ? "C:/lib" : "/lib";
    private static string OutsideTheLibrary => OperatingSystem.IsWindows() ? "D:/elsewhere" : "/elsewhere";

    private static RouteLookups StudioRoute(Destination destination) =>
        new(
            new Dictionary<int, Destination> { [42] = destination },
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            Array.Empty<(System.Text.RegularExpressions.Regex, Destination)>());

    private static RenamerEntity RoutableEntity(string folderPath) =>
        VideoEntity("My Film", VideoFile(1, "raw.mkv", folderPath: folderPath)) with { StudioId = 42 };

    [Fact]
    public async Task ARoutedItemLandsInsideTheLibrary_AndIsNotFlagged()
    {
        // The negative half of the off-library flag, and the reason it now needs stating on its own: a
        // destination chooses its root FROM Cove's library paths, so a routed item cannot land outside
        // the library at all. This case is what stops the flag being stuck ON — the positive half moved
        // to the in-place case below, which is the only shape that can still reach it.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(LibraryPath);
        port.SeedEntity(RoutableEntity($"{LibraryPath}/videos"));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title" };

        var inside = Assert.Single((await planner.PlanAsync(
            RenamerFileKind.Video, 10, opts, StudioRoute(Dests.At(LibraryPath, "archive")), default)).Items);

        // An ACTING item: a skip carries the flag's default and would read as "not flagged".
        Assert.Equal(RenamerStatus.Move, inside.Status);
        Assert.Equal($"{LibraryPath}/archive/My Film.mkv", inside.NewFullPath);
        Assert.False(inside.OffLibraryDestination);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task AFileAlreadyOutsideTheLibrary_RenamedInPlace_IsFlagged()
    {
        // The flag is keyed on where the file ENDS UP, not on whether a rule moved it there — and since
        // every destination now chooses a library path as its root, an item renamed in place OUTSIDE the
        // library is the only shape that can reach it. It is outside the scanned set for exactly the
        // reasons a moved file would be, so exempting it would be a line the user cannot predict.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(LibraryPath);
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv", folderPath: OutsideTheLibrary)));
        var planner = new RenamerPlanner(port);

        var item = Assert.Single((await planner.PlanAsync(
            RenamerFileKind.Video, 10, new RenamerOptions { FilenameTemplate = "$title" },
            RouteLookupsFixtures.RoutingNeutral, default)).Items);

        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.True(item.OffLibraryDestination);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task WithNoLibraryPathDeclaredAtAll_NothingIsFlagged_BecauseTheWarningWouldSayNothing()
    {
        // "Outside everything Cove scans" is a claim about a scanned set. With no library path there is
        // none, so the sentence would be true of every file at once — which is a badge on every row and
        // information in none of them. The host cannot scan in this state either, so it is a
        // misconfiguration to report at load (LogNoCoveConfiguration), never per row.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv", folderPath: OutsideTheLibrary)));
        var planner = new RenamerPlanner(port);
        // With no library path there is nowhere to move TO — every destination measures from one — so
        // the only acting shape left is an in-place rename, and only an acting item can carry the flag.
        var opts = new RenamerOptions { FilenameTemplate = "$title" };

        var item = Assert.Single((await planner.PlanAsync(
            RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default)).Items);

        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.False(item.OffLibraryDestination);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task MissingEntity_ReturnsEmptyPlan()
    {
        var port = new FakeRenamerDataPort();
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 999, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        Assert.Empty(plan.Items);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task PlanLoadedEntityAsync_MatchesLoadingPath_ItemForItem()
    {
        // Seed the SAME entity behind the loading path; plan it both ways and prove item-for-item
        // equality — the pure method and the load-then-plan method share identical plan logic.
        var entity = VideoEntity("My Film", VideoFile(1, "raw.mkv"));
        var port = new FakeRenamerDataPort();
        port.SeedEntity(entity);
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title" };

        var loaded = await planner.PlanLoadedEntityAsync(entity, opts, RouteLookupsFixtures.RoutingNeutral, default);
        var viaLoad = (await planner.PlanWithEntityAsync(RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default)).Plan;

        Assert.Equal(viaLoad.EntityId, loaded.EntityId);
        Assert.Equal(viaLoad.Kind, loaded.Kind);
        Assert.Equal(viaLoad.Items, loaded.Items);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task PlanLoadedEntityAsync_PerformsZeroLoads()
    {
        var entity = VideoEntity("My Film", VideoFile(1, "raw.mkv"));
        var port = new FakeRenamerDataPort();  // NOT seeded — proves no load is attempted
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanLoadedEntityAsync(entity, new RenamerOptions { FilenameTemplate = "$title" }, RouteLookupsFixtures.RoutingNeutral, default);

        Assert.Single(plan.Items);
        Assert.Equal(0, port.LoadEntityCallCount);
    }

    [Fact]
    public async Task PlanLoadedEntityAsync_GatedEntity_YieldsSameSkips_AsLoadingPath()
    {
        // An unorganized entity under the only-organized gate: SkipGated for every file, both ways.
        var entity = new RenamerEntity(
            EntityId: 10, Kind: RenamerFileKind.Video, Title: "Ungated", Code: null, StudioName: null,
            Date: null, Organized: false, Performers: [], TagRefs: [], Files: [VideoFile(1, "raw.mkv")]);
        var port = new FakeRenamerDataPort();
        port.SeedEntity(entity);
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title", OnlyOrganized = true };

        var loaded = await planner.PlanLoadedEntityAsync(entity, opts, RouteLookupsFixtures.RoutingNeutral, default);
        var viaLoad = (await planner.PlanWithEntityAsync(RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default)).Plan;

        Assert.Equal(RenamerStatus.SkipGated, Assert.Single(loaded.Items).Status);
        Assert.Equal(viaLoad.Items, loaded.Items);
    }

    // ── One item per file: no first-file-only assumption, none dropped ──────────────────────────────

    [Fact]
    public async Task TwoFileItem_ProducesTwoItems_OnePerFile()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(new RenamerEntity(
            EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [],
            Files: [PlainFile(1, "part1.mkv"), PlainFile(2, "part2.mkv")]));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        Assert.Equal(2, plan.Items.Count);
        Assert.Contains(plan.Items, i => i.FileId == 1);
        Assert.Contains(plan.Items, i => i.FileId == 2);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    // ── The advisory badges: Suffixed is true exactly when the collision suffix loop ran (attempt > 0),
    //    Sanitized exactly when the engine reported the name was cleaned. Both default false (additive),
    //    so an existing construction that never sets them keeps compiling. ────────────────────────────

    [Fact]
    public async Task FreeName_NotSuffixed()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(PlainEntity("My Film", PlainFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.False(item.Suffixed);
    }

    [Fact]
    public async Task FirstCandidateTaken_Suffixed_AndBasenameCarriesSuffix()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(PlainEntity("My Film", PlainFile(1, "raw.mkv")));
        // "My Film.mkv" is taken by another file → suffix loop runs (attempt > 0).
        port.SeedOccupied(folderId: 5, basename: "My Film.mkv", fileId: 99);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.True(item.Suffixed);
        Assert.Equal("My Film (1).mkv", item.NewBasename);
    }

    [Fact]
    public async Task IllegalChars_Sanitized()
    {
        var port = new FakeRenamerDataPort();
        // A title with an illegal filename char (':') → the engine sanitizes it out.
        port.SeedEntity(PlainEntity("My: Film", PlainFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.True(item.Sanitized);
        // The illegal ':' is gone from the rendered basename.
        Assert.DoesNotContain(':', item.NewBasename);
    }

    [Fact]
    public async Task CleanName_NotSanitized()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(PlainEntity("My Film", PlainFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.False(item.Sanitized);
    }

    // ── PHASE A load de-duplication: the batch runner reads each file's free-space SizeBytes off the
    //    entity the planner already loaded, instead of loading it a second time. The load counter on
    //    FakeRenamerDataPort is the seam every PHASE A load flows through, so N ids must produce exactly
    //    N loads (not 2N), and the surfaced entity must still carry the seeded sizes so the de-dup
    //    cannot silently drop them. ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PhaseA_LoadsEachEntityExactlyOnce()
    {
        const int n = 4;
        var port = new FakeRenamerDataPort();
        for (int i = 1; i <= n; i++)
        {
            port.SeedEntity(new RenamerEntity(
                EntityId: i, Kind: RenamerFileKind.Video, Title: $"Film {i}", Code: null, StudioName: null,
                Date: null, Organized: true, Performers: [], TagRefs: [],
                Files: [PlainFile(i, $"raw {i}.mkv", sizeBytes: 100L * i)]));
        }
        var planner = new RenamerPlanner(port);

        for (int i = 1; i <= n; i++)
        {
            _ = await planner.PlanWithEntityAsync(RenamerFileKind.Video, i, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);
        }

        Assert.Equal(n, port.LoadEntityCallCount);
    }

    [Fact]
    public async Task PhaseA_SurfacesSeededSizesForTheFreeSpaceSum()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(new RenamerEntity(
            EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [],
            Files: [PlainFile(1, "part1.mkv", sizeBytes: 1234), PlainFile(2, "part2.mkv", sizeBytes: 5678)]));
        var planner = new RenamerPlanner(port);

        var (_, entity) = await planner.PlanWithEntityAsync(
            RenamerFileKind.Video, 10, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        Assert.Equal(1, port.LoadEntityCallCount);
        Assert.NotNull(entity);
        var sizeByFileId = entity!.Files.ToDictionary(f => f.FileId, f => f.SizeBytes);
        Assert.Equal(1234, sizeByFileId[1]);
        Assert.Equal(5678, sizeByFileId[2]);
        // A file id absent from the map contributes 0 to the free-space sum, exactly as before.
        Assert.Equal(0, sizeByFileId.GetValueOrDefault(999));
    }

    [Fact]
    public async Task PhaseA_MissingEntity_LoadsOnce_YieldsNullEntityAndEmptyPlan()
    {
        var port = new FakeRenamerDataPort();
        var planner = new RenamerPlanner(port);

        var (plan, entity) = await planner.PlanWithEntityAsync(
            RenamerFileKind.Video, 42, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        Assert.Equal(1, port.LoadEntityCallCount);
        Assert.Null(entity);
        Assert.Empty(plan.Items);
    }
}
