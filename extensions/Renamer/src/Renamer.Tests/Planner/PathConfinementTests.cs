using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the path-confinement gate on BOTH of its branches.
/// <para>
/// EMPTY ALLOWLIST — the narrow, original confinement a user with no configured allowed roots gets,
/// where the file's own source folder is the sole implicit root: a benign relative subfolder resolves
/// UNDER that folder and is accepted; a "../.." traversal or an absolute/rooted folder template is
/// REJECTED; an over-FullPathMax absolute target is rejected. The sibling case ("root" vs "rootEvil")
/// proves the prefix check is boundary-aware.
/// </para>
/// <para>
/// NON-EMPTY ALLOWLIST — the inverted gate: when one or more allowed roots are configured, a target
/// folder (possibly rooted) is accepted ONLY when it normalizes to a path under some allowed root. The
/// "<c>..</c>" defense survives — a rooted target with parent-traversal is collapsed by
/// <see cref="Path.GetFullPath(string, string)"/> BEFORE the containment check, so an escape past the
/// only root is rejected.
/// </para>
/// PURE — no disk.
/// </summary>
/// <remarks>
/// The empty-allowlist cases are driven through the allowlist-form <c>Resolve</c> with an EMPTY root
/// list, which is the only entry point production has (<c>RenamerPlanner</c> calls that one form and
/// nothing else). A single-root overload used to exist for those cases alone; reaching the same branch
/// the way the planner reaches it means a regression in the branch selection itself — not just in the
/// confinement maths — fails here.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class PathConfinementTests
{
    private const string Root = "media/videos";

    // An absolute allowed root that exists in path-syntax terms on the current OS.
    private static string AllowedRoot => OperatingSystem.IsWindows() ? @"D:\media" : "/srv/media";

    private static IReadOnlyList<string> Roots => [AllowedRoot];

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

    // ── The non-empty-allowlist branch: accepted ONLY when the target normalizes under some root ────

    [Fact]
    public void RootedTarget_UnderAllowedRoot_Accepted()
    {
        var dest = OperatingSystem.IsWindows() ? @"D:\media\Acme\2024" : "/srv/media/Acme/2024";

        var r = PathConfinement.Resolve(
            Roots, legacySourceRoot: "media/videos", destinationFolder: dest,
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/Acme/2024", r.TargetFolderPath);
    }

    [Fact]
    public void RootedTarget_UnderNoAllowedRoot_Rejected()
    {
        var dest = OperatingSystem.IsWindows() ? @"C:\Windows\System32" : "/etc/passwd.d";

        var r = PathConfinement.Resolve(
            Roots, legacySourceRoot: "media/videos", destinationFolder: dest,
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.False(r.Accepted);
        Assert.Contains("not under any allowed root", r.Reason);
    }

    [Fact]
    public void RootedTarget_ParentTraversal_CollapsesThenFailsContainment_Rejected()
    {
        // "D:/media/../../etc" collapses to "D:/etc" (or "/srv/media/../../etc" -> "/etc"), which is
        // NOT under the allowed root — the ".." is resolved BEFORE the containment check.
        var dest = OperatingSystem.IsWindows() ? @"D:\media\..\..\etc" : "/srv/media/../../etc";

        var r = PathConfinement.Resolve(
            Roots, legacySourceRoot: "media/videos", destinationFolder: dest,
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.False(r.Accepted);
        Assert.Contains("not under any allowed root", r.Reason);
    }

    [Fact]
    public void RootedSibling_NotMistakenForChild_Rejected()
    {
        // "D:/mediaEvil" shares the textual prefix of "D:/media" but is a sibling, not a child.
        var dest = OperatingSystem.IsWindows() ? @"D:\mediaEvil\loot" : "/srv/mediaEvil/loot";

        var r = PathConfinement.Resolve(
            Roots, legacySourceRoot: "media/videos", destinationFolder: dest,
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.False(r.Accepted);
        Assert.Contains("not under any allowed root", r.Reason);
    }

    [Fact]
    public void RelativeTarget_ResolvesUnderSource_AcceptedOnlyWhenUnderARoot()
    {
        // A relative destination is resolved under legacySourceRoot; it is accepted only when that
        // resolved path lands under a configured root. Here the source IS the root, so it passes.
        var r = PathConfinement.Resolve(
            Roots, legacySourceRoot: AllowedRoot.Replace('\\', '/'), destinationFolder: "Acme/2024",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/Acme/2024", r.TargetFolderPath);
    }

    [Fact]
    public void RelativeTarget_ResolvingOutsideEveryRoot_Rejected()
    {
        // Source folder is NOT under any allowed root, so a benign relative subfolder still lands
        // outside every root and is rejected.
        var r = PathConfinement.Resolve(
            Roots, legacySourceRoot: "media/videos", destinationFolder: "Acme/2024",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.False(r.Accepted);
        Assert.Contains("not under any allowed root", r.Reason);
    }

    [Fact]
    public void OverFullPathMax_UnderAllowedRoot_Rejected()
    {
        var opts = new RenamerOptions { FullPathMax = 40 };
        var dest = OperatingSystem.IsWindows() ? @"D:\media\Acme" : "/srv/media/Acme";

        var r = PathConfinement.Resolve(
            Roots, legacySourceRoot: "media/videos", destinationFolder: dest,
            newBasename: new string('x', 300) + ".mkv", options: opts);

        Assert.False(r.Accepted);
        Assert.Contains("FullPathMax", r.Reason);
    }

    // The empty-roots fallback, asserted from the allowlist side: it must reproduce the original
    // source-confine behavior verbatim. The two cases below therefore restate expectations the
    // empty-allowlist cases above also hold — deliberately, since what they pin is the EQUIVALENCE of
    // the two branches at their boundary, which neither branch's own cases can show alone.

    [Fact]
    public void EmptyRoots_RootedDestination_RejectedWithLegacyMessage()
    {
        var dest = OperatingSystem.IsWindows() ? @"D:\media\Acme" : "/srv/media/Acme";

        var r = PathConfinement.Resolve(
            allowedRoots: [], legacySourceRoot: "media/videos", destinationFolder: dest,
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.False(r.Accepted);
        Assert.Equal("folder template is an absolute/rooted path", r.Reason);
    }

    [Fact]
    public void EmptyRoots_BenignRelativeSubfolder_ResolvesUnderSource_Accepted()
    {
        var r = PathConfinement.Resolve(
            allowedRoots: [], legacySourceRoot: "media/videos", destinationFolder: "Acme/2024",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/videos/Acme/2024", r.TargetFolderPath);
    }
}
