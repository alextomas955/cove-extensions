using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// The FullPathMax re-check measures the destination a matched RULE named, not the source folder.
/// The load-bearing assertion is the contrast — the SAME rendered name FITS under a short source folder
/// but OVERFLOWS under a deep rule destination, so the over-long case becomes a
/// skip-with-reason at PREVIEW (not a move-time crash). Driven through <c>RenamerPlanner.PlanAsync</c>
/// (the wiring), reusing the OS-aware Root style of <c>PathConfinementTests</c>. PURE — no disk.
/// </summary>
[Trait("Tier", "L0")]
public sealed class DestAnchoredMaxPathTests
{
    // A SHORT source folder and a DEEP routed root, so the same render fits under one and overflows the other.
    private static string ShortSource => OperatingSystem.IsWindows() ? @"C:\s" : "/s";
    private static string DeepRoot => OperatingSystem.IsWindows()
        ? @"D:\a\very\deeply\nested\destination\hierarchy\for\overflow"
        : "/a/very/deeply/nested/destination/hierarchy/for/overflow";

    private static string Fwd(string p) => p.Replace('\\', '/');

    private static RenamerFile VideoFile(string folderPath) =>
        new(FileId: 1, Kind: RenamerFileKind.Video, Basename: "raw.mkv", ParentFolderId: 5,
            ParentFolderPath: Fwd(folderPath), Format: "mkv", Width: 1920, Height: 1080,
            Duration: 3600, VideoCodec: "h264", AudioCodec: "aac", FrameRate: 30);

    private static RenamerEntity Entity(string title, RenamerFile file) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: title, Code: "ABC-1", StudioName: "Acme",
            Date: new DateOnly(2024, 3, 2), Organized: true,
            Performers: [new RenamerPerformer(1, "Bob", false, null)], TagRefs: [], Files: [file],
            StudioId: 42);

    private static RouteLookups StudioLookup(Destination dest) =>
        new(
            new Dictionary<int, Destination> { [42] = dest },
            new Dictionary<int, Destination>(),
            new Dictionary<string, Destination>(StringComparer.Ordinal),
            Array.Empty<(System.Text.RegularExpressions.Regex, Destination)>());

    // A title chosen so its rendered absolute path FITS under the short source but OVERFLOWS the deep root.
    private static string Title => new('N', 60);

    // FullPathMax tuned between the two absolute lengths: short-source path < max < deep-root path.
    private const int Max = 90;

    // ── The planned basename does not move when the in-flight overflow warning is added ────────────
    //
    // A cross-volume move copies to a minted in-flight name 12 characters longer than the final one, so
    // the obvious fix is to subtract those 12 from the budget the reducer and this boundary fit against.
    // That is forbidden: LengthReducer drops fields and then hard-truncates, so a tighter budget
    // renames every file near the limit — users who did nothing wrong get a different result. These cases
    // pin the boundary itself, so that subtraction fails here rather than shipping.

    // Both spellings are FOUR characters, which is load-bearing: a Windows absolute root ("D:\dest") is
    // longer than its Unix twin ("/dest"), so a budget literal tuned on one platform would sit beside the
    // boundary rather than on it on the other.
    private static string BoundaryRoot => OperatingSystem.IsWindows() ? @"D:\d" : "/ddd";

    // The rule's whole destination: a root chosen from the library paths plus the one folder below it.
    // Written as one value because that is what a rule now holds — not a root some other setting
    // completes.
    private const string BoundaryFolder = "S";

    private static Destination BoundaryDestination => Dests.At(BoundaryRoot, BoundaryFolder);
    private const string BoundaryTitle = "Boundary";

    // The rendered basename, written out rather than composed from the template: "$title" plus the source
    // file's own extension. An expectation computed from the engine agrees with it forever.
    private const string BoundaryBasename = "Boundary.mkv";

    // The absolute path the item resolves to: root(4) + "/" + folder(1) + "/" + basename(12). Hand-counted,
    // and proved by the reject case below, whose reason names the length the planner actually measured.
    private const int BoundaryAbsoluteLength = 19;

    private static RenamerOptions BoundaryOptions(int fullPathMax) => new()
    {
        FilenameTemplate = "$title",
        AllowedRoots = [BoundaryRoot],
        FullPathMax = fullPathMax,
    };

    private static async Task<RenamerPlanItem> PlanAtBudgetAsync(int fullPathMax)
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(BoundaryRoot);   // the rule's chosen root, so it is still one to choose
        port.SeedEntity(Entity(BoundaryTitle, VideoFile(ShortSource)));
        var plan = await new RenamerPlanner(port).PlanAsync(
            RenamerFileKind.Video, 10, BoundaryOptions(fullPathMax), StudioLookup(BoundaryDestination), default);
        return Assert.Single(plan.Items);
    }

    [Fact]
    public async Task AtTheBudgetExactly_TheItemIsPlanned_WithTheBasenameWrittenBelow()
    {
        var item = await PlanAtBudgetAsync(BoundaryAbsoluteLength);

        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(BoundaryBasename, item.NewBasename);
    }

    [Fact]
    public async Task OneCharacterOverTheBudget_IsRejected_NamingTheLengthItMeasured()
    {
        var item = await PlanAtBudgetAsync(BoundaryAbsoluteLength - 1);

        // The length refusal has its own status, so a user reading the dry run is told to shorten
        // something rather than to wait for a name conflict that does not exist.
        Assert.Equal(RenamerStatus.SkipTooLong, item.Status);
        // Transcribed from PathConfinement's own message. It is also what proves BoundaryAbsoluteLength
        // is the real absolute length on this platform rather than an arithmetic slip.
        Assert.Equal(
            $"resolved absolute path length {BoundaryAbsoluteLength} exceeds FullPathMax {BoundaryAbsoluteLength - 1}",
            item.Reason);
    }

    [Fact]
    public async Task SuffixedPastTheBudget_IsRefused_NamingTheLengthItMeasured()
    {
        // The one case the pair above cannot reach: the rendered name fits the budget EXACTLY, and the
        // duplicate-suffix loop is what pushes it over. That loop runs after the budget check, so before a
        // second measurement this item planned as an ordinary rename and was written to disk at a path the
        // configured budget forbids — at the budget it is four characters, but the suffix format is
        // user-configurable and the loop runs to a thousand attempts, so the overrun has no fixed size.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryRoot(BoundaryRoot);
        port.SeedEntity(Entity(BoundaryTitle, VideoFile(ShortSource)));

        // Two seeds are what make the loop run at all. The destination folder must already have an id,
        // because it is looked up rather than minted during planning and the lookup returns null unless
        // seeded — which is exactly why the two boundary cases above never enter the loop. And the name
        // must be held by a DIFFERENT file, since the collision check excludes the item's own row.
        const int TargetFolderId = 77;
        port.SeedFolder(Fwd(BoundaryRoot) + "/" + BoundaryFolder, TargetFolderId);
        port.SeedOccupied(TargetFolderId, BoundaryBasename, fileId: 999);

        var plan = await new RenamerPlanner(port).PlanAsync(
            RenamerFileKind.Video, 10, BoundaryOptions(BoundaryAbsoluteLength),
            StudioLookup(BoundaryDestination), default);
        var item = Assert.Single(plan.Items);

        Assert.Equal(RenamerStatus.SkipTooLong, item.Status);
        // Transcribed from PathConfinement's own message, as the reject case above does. Here it carries a
        // second load: naming a length four over the budget is what proves the re-measure ran on the
        // SUFFIXED name and against the same basis as the first check, rather than repeating the first
        // measurement or measuring from a different folder.
        Assert.Equal(
            $"resolved absolute path length {BoundaryAbsoluteLength + 4} exceeds FullPathMax {BoundaryAbsoluteLength}",
            item.Reason);
    }

    [Fact]
    public async Task WithTheInFlightHeadroomAdded_TheBasenameIsByteIdentical()
    {
        // The pair is the assertion: the same literal at the boundary and the minted segment's length
        // above it. Had that length been subtracted from the budget rather than warned about, the boundary
        // case above would be a skip and this one would be the only survivor. Read from the minter's own
        // declaration, so a narrowing of the minted name moves this case with it.
        var item = await PlanAtBudgetAsync(
            BoundaryAbsoluteLength + CrossVolumeMover.InFlightSuffixLength);

        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(BoundaryBasename, item.NewBasename);
    }

    [Fact]
    public async Task RoutedDeepDestination_Overflows_SkipWithLengthReason()
    {
        var port = new FakeRenamerDataPort();
        // The file SITS in the short source folder, but routes to the DEEP root.
        port.SeedLibraryRoot(DeepRoot);
        port.SeedEntity(Entity(Title, VideoFile(ShortSource)));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            AllowedRoots = [ShortSource, DeepRoot],
            FullPathMax = Max,
        };

        var plan = await planner.PlanAsync(
            RenamerFileKind.Video, 10, opts, StudioLookup(Dests.At(DeepRoot, "Sorted")), default);

        var item = Assert.Single(plan.Items);
        // Measured against the deep rule destination → the absolute path overflows → skip at preview.
        Assert.Equal(RenamerStatus.SkipTooLong, item.Status);
        Assert.Contains("FullPathMax", item.Reason);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task SameRender_FitsUnderShortSource_WhenNotRouted()
    {
        var port = new FakeRenamerDataPort();
        // The IDENTICAL render under the SHORT source folder (no route) fits within the same FullPathMax —
        // proving the overflow above is caused by the deep rule destination, not the render itself.
        port.SeedLibraryRoot(ShortSource);   // the unrouted anchor: the library path holding the file
        port.SeedEntity(Entity(Title, VideoFile(ShortSource)));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            AllowedRoots = [ShortSource],
            FullPathMax = Max,
        };

        // Empty lookups → no rule matched → the default template, anchored on the short library path.
        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        // Measured from the library path holding the file, which is what "no rule matched" resolves to.
        Assert.Equal(Fwd(ShortSource), item.ResolvedDestinationRoot);
        Assert.Empty(port.ApplyAndSaveCalls);
    }
}
