using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the allowlist NARROWING: a target already inside its destination anchor is accepted only
/// when it also lands under some configured allowed root. The "<c>..</c>" defense survives - a
/// traversal is collapsed by <see cref="Path.GetFullPath(string, string)"/> BEFORE the containment
/// check, so an escape is rejected against the anchor itself, whether or not a narrowing list is
/// configured. The empty list narrows nothing. PURE - no disk access.
/// </summary>
[Trait("Tier", "L0")]
public sealed class PathConfinementAllowlistTests
{
    // An absolute allowed root that exists in path-syntax terms on the current OS.
    private static string Root => OperatingSystem.IsWindows() ? @"D:\media" : "/srv/media";

    private static string Fwd(string p) => p.Replace('\\', '/');

    private static IReadOnlyList<string> Roots => [Root];

    [Fact]
    public void TargetUnderTheAnchorAndUnderAnAllowedRoot_Accepted()
    {
        var r = PathConfinement.Resolve(
            Roots, anchor: Fwd(Root), destinationFolder: "Acme/2024",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/Acme/2024", r.TargetFolderPath);
    }

    [Fact]
    public void TargetUnderTheAnchorButUnderNoAllowedRoot_Rejected()
    {
        // The anchor is a library path the narrowing list does not cover, so the rename may not write
        // there even though the destination is inside the library.
        var anchor = OperatingSystem.IsWindows() ? "E:/other" : "/srv/other";

        var r = PathConfinement.Resolve(
            Roots, anchor: anchor, destinationFolder: "Acme/2024",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
        Assert.Contains("not under any allowed root", r.Reason);
    }

    [Fact]
    public void ANarrowerRootThanTheAnchor_AcceptsOnlyTheSubtreeItNames()
    {
        // The only thing the list can still express: rename inside this subtree only.
        var narrow = Fwd(Root) + "/Acme";

        var inside = PathConfinement.Resolve(
            [narrow], anchor: Fwd(Root), destinationFolder: "Acme/2024",
            newBasename: "film.mkv", options: new RenamerOptions());
        var outside = PathConfinement.Resolve(
            [narrow], anchor: Fwd(Root), destinationFolder: "Other/2024",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.True(inside.Accepted);
        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, outside.Rejection);
    }

    [Fact]
    public void ParentTraversal_CollapsesThenFailsContainment_Rejected()
    {
        // "D:/media/../../etc" collapses to "D:/etc" (or "/srv/media/../../etc" -> "/etc"), which is
        // NOT under the anchor - the ".." is resolved BEFORE the containment check.
        var r = PathConfinement.Resolve(
            Roots, anchor: Fwd(Root), destinationFolder: "../../etc",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
        Assert.Contains("escapes its destination root", r.Reason);
    }

    [Fact]
    public void Sibling_NotMistakenForChild_Rejected()
    {
        // "D:/mediaEvil" shares the textual prefix of "D:/media" but is a sibling, not a child.
        var r = PathConfinement.Resolve(
            Roots, anchor: Fwd(Root), destinationFolder: "../mediaEvil/loot",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
    }

    [Fact]
    public void OverFullPathMax_UnderAllowedRoot_RejectedAsTooLong()
    {
        var opts = new RenamerOptions { FullPathMax = 40 };

        var r = PathConfinement.Resolve(
            Roots, anchor: Fwd(Root), destinationFolder: "Acme",
            newBasename: new string('x', 300) + ".mkv", options: opts);

        Assert.Equal(PathConfinement.ConfinementRejection.TooLong, r.Rejection);
        Assert.Contains("FullPathMax", r.Reason);
    }

    [Fact]
    public void EmptyRoots_NarrowNothing_AnchorContainmentStillHolds()
    {
        var accepted = PathConfinement.Resolve(
            allowedRoots: [], anchor: "media/videos", destinationFolder: "Acme/2024",
            newBasename: "film.mkv", options: new RenamerOptions());
        var escaping = PathConfinement.Resolve(
            allowedRoots: [], anchor: "media/videos", destinationFolder: "../escape",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.True(accepted.Accepted);
        Assert.EndsWith("/media/videos/Acme/2024", accepted.TargetFolderPath);
        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, escaping.Rejection);
    }
}
