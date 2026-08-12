using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// The per-row in-flight overflow flag as a PAGE reads it: driven through
/// <see cref="ScanRowPager.PageAsync"/> rather than through <see cref="ScanRow.From"/>, because the
/// projection classifies nothing itself and a test of it would only re-check the value it was handed.
/// What is at stake here is the composition — that the page computes the flag at all, and against the
/// same budget the planner just planned against.
/// <para>
/// PURE: a fake port, string-only path math and a synthetic mount table. No disk, no DB, and no
/// dependence on the runner's own volumes.
/// </para>
/// </summary>
[Trait("Tier", "L0")]
public sealed class ScanRowOverflowFlagTests
{
    // Volume identity is per-platform: the path root on Windows, the enclosing mount point on Unix. Both
    // spellings are FOUR characters, which is what lets one arithmetic land on the same absolute length on
    // both — a Windows root ("D:\d") is otherwise longer than its Unix twin.
    private static string SourceRoot => OperatingSystem.IsWindows() ? @"C:\s" : "/sss";
    private static string DestRoot => OperatingSystem.IsWindows() ? @"D:\d" : "/ddd";

    // On Unix every path under "/" shares one volume, so the destination root has to BE a mount for the
    // cross-volume arm to exist. On Windows the drive letters already carry that difference.
    private static readonly IReadOnlyCollection<string>? Mounts =
        OperatingSystem.IsWindows() ? null : ["/", "/ddd"];

    // Above the shipped 259 nothing would fit; well under it the field-dropping reducer stays out of the
    // way. Every length below is positioned relative to this and to the minted segment's own declaration.
    private const int Budget = 60;

    private const string Folder = "S";

    // root(4) + "/" + Folder(1) + "/" — the fixed part of every resolved path below. Hand-counted, and
    // asserted against the planner's own output before any flag is read.
    private const int PathPrefixLength = 7;

    private const string Extension = ".mkv";

    /// <summary>A title whose rendered absolute path is exactly <paramref name="pathLength"/> characters.</summary>
    private static string TitleForPathLength(int pathLength) =>
        new('a', pathLength - PathPrefixLength - Extension.Length);

    private const int SourceStudioId = 42;
    private const int DestStudioId = 43;

    private static RenamerFile VideoFile(int fileId) =>
        new(FileId: fileId, Kind: RenamerFileKind.Video, Basename: $"raw{fileId}{Extension}",
            ParentFolderId: 5, ParentFolderPath: SourceRoot.Replace('\\', '/'), Format: "mkv");

    private static RenamerEntity Entity(int entityId, string title, int studioId) =>
        new(EntityId: entityId, Kind: RenamerFileKind.Video, Title: title, Code: null,
            StudioName: "Acme", Date: null, Organized: true, Performers: [], TagRefs: [],
            Files: [VideoFile(entityId)], StudioId: studioId);

    private static readonly RenamerOptions Options = new()
    {
        FilenameTemplate = "$title",
        FolderTemplate = Folder,
        AllowedRoots = [SourceRoot, DestRoot],
        FullPathMax = Budget,
    };

    // Routing by studio, so one fixture reaches a cross-volume destination and a same-volume one without
    // the two differing in any other way.
    private static readonly RouteLookups Lookups = new(
        StudioIdToDest: new Dictionary<int, string> { [DestStudioId] = DestRoot, [SourceStudioId] = SourceRoot },
        TagIdToDest: new Dictionary<int, string>(),
        PathExactToDest: new Dictionary<string, string>(StringComparer.Ordinal),
        PathRegexRules: []);

    [Fact]
    public async Task PagedRows_CarryTheOverflowFlag_OnlyForTheCrossVolumeRowPastTheBoundary()
    {
        // The longest final path whose cross-volume copy still fits: the copy is minted
        // CrossVolumeMover.InFlightSuffixLength characters longer beside the destination before being
        // promoted, and the planner budgets only the final path.
        int longestThatFits = Budget - CrossVolumeMover.InFlightSuffixLength;

        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(1, TitleForPathLength(Budget), DestStudioId));
        port.SeedEntity(Entity(2, TitleForPathLength(longestThatFits), DestStudioId));
        port.SeedEntity(Entity(3, TitleForPathLength(Budget), SourceStudioId));
        port.SeedAllIds(RenamerFileKind.Video, 1, 2, 3);

        var pager = new ScanRowPager(new RenamerPlanner(port), port, Mounts);
        var page = await pager.PageAsync(
            [RenamerFileKind.Video], cursor: null, take: 10, query: null, bucket: null,
            Options, Lookups, default);

        var byFileId = page.Rows.ToDictionary(r => r.FileId);
        Assert.Equal(3, byFileId.Count);

        // The lengths and the volumes the three rows claim, read off the planner's own output. Without
        // these a slip in the path arithmetic would put every row on the same side of the boundary and
        // leave three false flags looking like a correct answer.
        Assert.Equal(Budget, byFileId[1].NewFullPath.Length);
        Assert.Equal(longestThatFits, byFileId[2].NewFullPath.Length);
        Assert.Equal(Budget, byFileId[3].NewFullPath.Length);
        Assert.False(VolumeClassifier.SameVolume(byFileId[1].OldFullPath, byFileId[1].NewFullPath, Mounts));
        Assert.True(VolumeClassifier.SameVolume(byFileId[3].OldFullPath, byFileId[3].NewFullPath, Mounts));

        // Every row is a real acting plan, so no flag below is false merely because its row was skipped.
        Assert.All(page.Rows, r => Assert.Equal(RenamerStatus.Move, r.Status));

        Assert.True(byFileId[1].InFlightPathOverflow);
        Assert.False(byFileId[2].InFlightPathOverflow);
        Assert.False(byFileId[3].InFlightPathOverflow);
    }
}
