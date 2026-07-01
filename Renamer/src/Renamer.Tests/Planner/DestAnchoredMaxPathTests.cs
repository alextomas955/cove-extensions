using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves ROUTE-08 / D-06: the FullPathMax re-check re-anchors on the ROUTED destination root, not the
/// source folder. The load-bearing assertion is the contrast — the SAME rendered name FITS under a
/// short source folder but OVERFLOWS under a deep routed root, so the over-long case becomes a
/// skip-with-reason at PREVIEW (not a move-time crash). Driven through <c>RenamerPlanner.PlanAsync</c>
/// (the wiring), reusing the OS-aware Root style of <c>PathConfinementAllowlistTests</c>. PURE — no disk.
/// </summary>
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
            Performers: [new RenamerPerformer(1, "Bob", false, null)], Tags: [], Files: [file],
            StudioId: 42);

    private static RouteLookups StudioLookup(string dest) =>
        new(
            new Dictionary<int, string> { [42] = dest },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal),
            Array.Empty<(System.Text.RegularExpressions.Regex, string)>());

    private static RouteLookups EmptyLookup() =>
        new(
            new Dictionary<int, string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal),
            Array.Empty<(System.Text.RegularExpressions.Regex, string)>());

    // A title chosen so its rendered absolute path FITS under the short source but OVERFLOWS the deep root.
    private static string Title => new('N', 60);

    // FullPathMax tuned between the two absolute lengths: short-source path < max < deep-root path.
    private const int Max = 90;

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
