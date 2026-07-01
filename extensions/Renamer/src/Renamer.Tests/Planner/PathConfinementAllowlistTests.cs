using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the inverted allowlist gate: when one or more allowed roots are configured, a target
/// folder (possibly rooted) is accepted ONLY when it normalizes to a path under some allowed root.
/// The "<c>..</c>" defense survives — a rooted target with parent-traversal is collapsed by
/// <see cref="Path.GetFullPath(string, string)"/> BEFORE the containment check, so an escape past
/// the only root is rejected. The empty-roots fallback reproduces the original source-confine
/// behavior verbatim. PURE — no disk access.
/// </summary>
public sealed class PathConfinementAllowlistTests
{
    // An absolute allowed root that exists in path-syntax terms on the current OS.
    private static string Root => OperatingSystem.IsWindows() ? @"D:\media" : "/srv/media";

    private static IReadOnlyList<string> Roots => [Root];

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
        // NOT under the allowed root — the ".." is resolved BEFORE the containment check (GATE-02).
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
            Roots, legacySourceRoot: Root.Replace('\\', '/'), destinationFolder: "Acme/2024",
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
