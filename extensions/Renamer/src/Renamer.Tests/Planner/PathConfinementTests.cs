using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the path-confinement gate: a benign relative subfolder resolves UNDER
/// the destination's anchor and is accepted; a "../.." traversal or an absolute/rooted folder
/// template is REJECTED as <see cref="PathConfinement.ConfinementRejection.NotAllowed"/>; an
/// over-FullPathMax absolute target is rejected as
/// <see cref="PathConfinement.ConfinementRejection.TooLong"/>. The sibling case ("root" vs
/// "rootEvil") proves the prefix check is boundary-aware. PURE - no disk.
/// </summary>
[Trait("Tier", "L0")]
public sealed class PathConfinementTests
{
    private const string Root = "media/videos";

    private static PathConfinement.ConfinementResult Resolve(
        string folder, string basename = "film.mkv", RenamerOptions? options = null)
        => PathConfinement.Resolve([], Root, folder, basename, options ?? new RenamerOptions());

    [Fact]
    public void EmptyFolder_IsInPlace_Accepted_UnderRoot()
    {
        var r = Resolve("");

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/videos", r.TargetFolderPath);
    }

    [Fact]
    public void BenignRelativeSubfolder_Accepted_UnderRoot()
    {
        var r = Resolve("Acme/2024");

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/videos/Acme/2024", r.TargetFolderPath);
    }

    [Fact]
    public void ParentTraversal_Escape_Rejected()
    {
        var r = Resolve("../../escape");

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
        Assert.Contains("escapes", r.Reason);
    }

    [Fact]
    public void AbsoluteFolderTemplate_Rejected()
    {
        var abs = OperatingSystem.IsWindows() ? @"C:\Windows\System32" : "/etc";

        var r = Resolve(abs);

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
        Assert.Contains("not relative", r.Reason);
    }

    [Fact]
    public void Sibling_NotMistakenForChild_Rejected()
    {
        // Resolving "../videosEvil" from "media/videos" lands on a SIBLING "media/videosEvil"
        // whose absolute form shares the textual prefix of the root but is NOT under it.
        var r = Resolve("../videosEvil");

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
    }

    [Fact]
    public void OverFullPathMax_AbsoluteTarget_RejectedAsTooLong()
    {
        var r = Resolve("", new string('x', 300) + ".mkv", new RenamerOptions { FullPathMax = 40 });

        Assert.Equal(PathConfinement.ConfinementRejection.TooLong, r.Rejection);
        Assert.Contains("FullPathMax", r.Reason);
    }

    [Fact]
    public void EscapeAndOverLength_ReportsTheEscape_NotTheLength()
    {
        // A destination refused outright must never be reported as merely too long: the two ask a
        // user for opposite actions.
        var r = Resolve("../../escape", new string('x', 300) + ".mkv", new RenamerOptions { FullPathMax = 40 });

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
    }
}

/// <summary>
/// Proves <see cref="PathConfinement.ContainingRoot"/>: which library path a file is anchored on.
/// The longest match wins so a nested library path anchors on the nearer boundary; a path under none
/// of them has no anchor at all. PURE - no disk.
/// </summary>
[Trait("Tier", "L0")]
public sealed class ContainingRootTests
{
    [Fact]
    public void PathUnderARoot_ReturnsThatRoot()
        => Assert.Equal("/media", PathConfinement.ContainingRoot("/media/a/b.mkv", ["/media"]));

    [Fact]
    public void NestedRoots_LongestWins()
        => Assert.Equal(
            "/media/video",
            PathConfinement.ContainingRoot("/media/video/a.mkv", ["/media", "/media/video"]));

    [Fact]
    public void NestedRoots_DeclaredInEitherOrder_ReachTheSameAnswer()
        => Assert.Equal(
            PathConfinement.ContainingRoot("/media/video/a.mkv", ["/media", "/media/video"]),
            PathConfinement.ContainingRoot("/media/video/a.mkv", ["/media/video", "/media"]));

    [Fact]
    public void PathUnderNoRoot_ReturnsNull()
        => Assert.Null(PathConfinement.ContainingRoot("/elsewhere/a.mkv", ["/media"]));

    [Fact]
    public void NoRootsAtAll_ReturnsNull()
        => Assert.Null(PathConfinement.ContainingRoot("/media/a.mkv", []));

    [Fact]
    public void BlankEntry_IsIgnored_NotTreatedAsARootMatchingEverything()
        => Assert.Null(PathConfinement.ContainingRoot("/elsewhere/a.mkv", ["", "   "]));

    [Fact]
    public void Sibling_IsNotAMatch()
        => Assert.Null(PathConfinement.ContainingRoot("/mediaEvil/a.mkv", ["/media"]));

    [Fact]
    public void TheAnswerIsTheNormalizedEntry_NotTheOneSupplied()
    {
        // The one-time options conversion writes this return straight into the stored destination
        // root, which the panel re-checks against the library-path list it was offered.
        Assert.Equal(
            "D:/media",
            PathConfinement.ContainingRoot("D:/media/a.mkv", [@"D:\media\"]));
    }
}
