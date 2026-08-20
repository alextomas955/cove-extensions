using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// The FullPathMax re-check re-anchors on the ROUTED destination root, not the
/// source folder. The load-bearing assertion is the contrast — the SAME rendered name FITS under a
/// short source folder but OVERFLOWS under a deep routed root, so the over-long case becomes a
/// skip-with-reason at PREVIEW (not a move-time crash). Driven through <c>RenamerPlanner.PlanAsync</c>
/// (the wiring), reusing the OS-aware Root style of <c>PathConfinementAllowlistTests</c>. PURE — no disk.
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

    private static RouteLookups StudioLookup(string dest) =>
        new(
            new Dictionary<int, string> { [42] = dest },
            new Dictionary<int, string>(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            Array.Empty<(System.Text.RegularExpressions.Regex, string)>());

    private static RouteLookups EmptyLookup() =>
        new(
            new Dictionary<int, string>(),
            new Dictionary<int, string>(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            Array.Empty<(System.Text.RegularExpressions.Regex, string)>());

    // A title chosen so its rendered absolute path FITS under the short source but OVERFLOWS the deep root.
    private static string Title => new('N', 60);

    // FullPathMax tuned between the two absolute lengths: short-source path < max < deep-root path.
    private const int Max = 90;

    // ── D-02: the planned basename does not move when the in-flight overflow warning is added ──────
    //
    // A cross-volume move copies to a minted in-flight name 12 characters longer than the final one, so
    // the obvious fix is to subtract those 12 from the budget the reducer and this boundary fit against.
    // D-02 forbids exactly that: LengthReducer drops fields and then hard-truncates, so a tighter budget
    // renames every file near the limit — users who did nothing wrong get a different result. These cases
    // pin the boundary itself, so that subtraction fails here rather than shipping.

    // Both spellings are FOUR characters, which is load-bearing: a Windows absolute root ("D:\dest") is
    // longer than its Unix twin ("/dest"), so a budget literal tuned on one platform would sit beside the
    // boundary rather than on it on the other.
    private static string BoundaryRoot => OperatingSystem.IsWindows() ? @"D:\d" : "/ddd";

    private const string BoundaryFolder = "S";
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
        FolderTemplate = BoundaryFolder,
        AllowedRoots = [BoundaryRoot],
        FullPathMax = fullPathMax,
    };

    private static async Task<RenamerPlanItem> PlanAtBudgetAsync(int fullPathMax)
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(BoundaryTitle, VideoFile(ShortSource)));
        var plan = await new RenamerPlanner(port).PlanAsync(
            RenamerFileKind.Video, 10, BoundaryOptions(fullPathMax), StudioLookup(BoundaryRoot), default);
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

        Assert.Equal(RenamerStatus.SkipCollision, item.Status);
        // Transcribed from PathConfinement's own message. It is also what proves BoundaryAbsoluteLength
        // is the real absolute length on this platform rather than an arithmetic slip.
        Assert.Equal(
            $"resolved absolute path length {BoundaryAbsoluteLength} exceeds FullPathMax {BoundaryAbsoluteLength - 1}",
            item.Reason);
    }

    [Fact]
    public async Task WithTheInFlightHeadroomAdded_TheBasenameIsByteIdentical()
    {
        // The pair is the D-02 assertion: the same literal at the boundary and the minted segment's length
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
        port.SeedEntity(Entity(Title, VideoFile(ShortSource)));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            AllowedRoots = [ShortSource, DeepRoot],
            FullPathMax = Max,
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, StudioLookup(DeepRoot), default);

        var item = Assert.Single(plan.Items);
        // Re-anchored on the deep routed root → the absolute path overflows → skip-with-reason at preview.
        Assert.Equal(RenamerStatus.SkipCollision, item.Status);
        Assert.Contains("FullPathMax", item.Reason);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task SameRender_FitsUnderShortSource_WhenNotRouted()
    {
        var port = new FakeRenamerDataPort();
        // The IDENTICAL render under the SHORT source folder (no route) fits within the same FullPathMax —
        // proving the overflow above is caused by the deep ROUTED anchor, not the render itself.
        port.SeedEntity(Entity(Title, VideoFile(ShortSource)));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            AllowedRoots = [ShortSource],
            FullPathMax = Max,
        };

        // Empty lookups → SourceConfine → anchored on the short source folder.
        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, EmptyLookup(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Null(item.ResolvedDestinationRoot);
        Assert.Empty(port.SaveCalls);
    }
}
