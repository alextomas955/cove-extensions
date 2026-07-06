using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the path-confinement gate: a benign relative subfolder resolves UNDER
/// the source file's original parent directory and is accepted; a "../.." traversal or an
/// absolute/rooted folder template is REJECTED; an over-FullPathMax absolute target is rejected.
/// The sibling case ("root" vs "rootEvil") proves the prefix check is boundary-aware. PURE — no disk.
/// </summary>
public sealed class PathConfinementTests
{
    private const string Root = "media/videos";

    [Fact]
    public void EmptyFolder_IsInPlace_Accepted_UnderRoot()
    {
        var r = PathConfinement.Resolve(Root, relativeFolder: "", newBasename: "film.mkv", new RenamerOptions());

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/videos", r.TargetFolderPath);
    }

    [Fact]
    public void BenignRelativeSubfolder_Accepted_UnderRoot()
    {
        var r = PathConfinement.Resolve(Root, relativeFolder: "Acme/2024", newBasename: "film.mkv", new RenamerOptions());

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/videos/Acme/2024", r.TargetFolderPath);
    }

    [Fact]
    public void ParentTraversal_Escape_Rejected()
    {
        var r = PathConfinement.Resolve(Root, relativeFolder: "../../escape", newBasename: "film.mkv", new RenamerOptions());

        Assert.False(r.Accepted);
        Assert.Contains("escapes", r.Reason);
    }

    [Fact]
    public void AbsoluteFolderTemplate_Rejected()
    {
        var abs = OperatingSystem.IsWindows() ? @"C:\Windows\System32" : "/etc";
        var r = PathConfinement.Resolve(Root, relativeFolder: abs, newBasename: "film.mkv", new RenamerOptions());

        Assert.False(r.Accepted);
        Assert.Contains("absolute", r.Reason);
    }

    [Fact]
    public void Sibling_NotMistakenForChild_Rejected()
    {
        // Resolving "../videosEvil" from "media/videos" lands on a SIBLING "media/videosEvil"
        // whose absolute form shares the textual prefix of the root but is NOT under it.
        var r = PathConfinement.Resolve(Root, relativeFolder: "../videosEvil", newBasename: "film.mkv", new RenamerOptions());

        Assert.False(r.Accepted);
    }

    [Fact]
    public void OverFullPathMax_AbsoluteTarget_Rejected()
    {
        var opts = new RenamerOptions { FullPathMax = 40 };
        var r = PathConfinement.Resolve(Root, relativeFolder: "", newBasename: new string('x', 300) + ".mkv", opts);

        Assert.False(r.Accepted);
        Assert.Contains("FullPathMax", r.Reason);
    }
}
