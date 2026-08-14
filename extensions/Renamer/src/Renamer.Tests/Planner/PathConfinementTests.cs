using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the path-confinement gate on its EMPTY-ALLOWLIST branch — the narrow, original confinement a
/// user with no configured allowed roots gets, where the file's own source folder is the sole implicit
/// root: a benign relative subfolder resolves UNDER that folder and is accepted; a "../.." traversal or
/// an absolute/rooted folder template is REJECTED; an over-FullPathMax absolute target is rejected. The
/// sibling case ("root" vs "rootEvil") proves the prefix check is boundary-aware. PURE — no disk.
/// </summary>
/// <remarks>
/// Driven through the allowlist-form <c>Resolve</c> with an EMPTY root list, which is the only entry
/// point production has (<c>RenamerPlanner</c> calls that one form and nothing else). A single-root
/// overload used to exist for these cases alone; reaching the same branch the way the planner reaches it
/// means a regression in the branch selection itself — not just in the confinement maths — fails here.
/// <see cref="PathConfinementAllowlistTests"/> covers the non-empty-allowlist branch.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class PathConfinementTests
{
    private const string Root = "media/videos";

    /// <summary>The empty allowed-root list is the whole point of this class, so it is named once here.</summary>
    private static PathConfinement.ConfinementResult ResolveWithNoAllowedRoots(
        string destinationFolder, string newBasename, RenamerOptions? options = null) =>
        PathConfinement.Resolve(
            allowedRoots: [],
            legacySourceRoot: Root,
            destinationFolder: destinationFolder,
            newBasename: newBasename,
            options ?? new RenamerOptions());

    [Fact]
    public void EmptyFolder_IsInPlace_Accepted_UnderRoot()
    {
        var r = ResolveWithNoAllowedRoots(destinationFolder: "", newBasename: "film.mkv");

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/videos", r.TargetFolderPath);
    }

    [Fact]
    public void BenignRelativeSubfolder_Accepted_UnderRoot()
    {
        var r = ResolveWithNoAllowedRoots(destinationFolder: "Acme/2024", newBasename: "film.mkv");

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/videos/Acme/2024", r.TargetFolderPath);
    }

    [Fact]
    public void ParentTraversal_Escape_Rejected()
    {
        var r = ResolveWithNoAllowedRoots(destinationFolder: "../../escape", newBasename: "film.mkv");

        Assert.False(r.Accepted);
        Assert.Contains("escapes", r.Reason);
    }

    [Fact]
    public void AbsoluteFolderTemplate_Rejected()
    {
        var abs = OperatingSystem.IsWindows() ? @"C:\Windows\System32" : "/etc";
        var r = ResolveWithNoAllowedRoots(destinationFolder: abs, newBasename: "film.mkv");

        Assert.False(r.Accepted);
        Assert.Contains("absolute", r.Reason);
    }

    [Fact]
    public void Sibling_NotMistakenForChild_Rejected()
    {
        // Resolving "../videosEvil" from "media/videos" lands on a SIBLING "media/videosEvil"
        // whose absolute form shares the textual prefix of the root but is NOT under it.
        var r = ResolveWithNoAllowedRoots(destinationFolder: "../videosEvil", newBasename: "film.mkv");

        Assert.False(r.Accepted);
    }

    [Fact]
    public void OverFullPathMax_AbsoluteTarget_Rejected()
    {
        var r = ResolveWithNoAllowedRoots(
            destinationFolder: "", newBasename: new string('x', 300) + ".mkv",
            options: new RenamerOptions { FullPathMax = 40 });

        Assert.False(r.Accepted);
        Assert.Contains("FullPathMax", r.Reason);
    }
}
