using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the path-confinement gate, and the anchor picker it shares its containment rule with.
/// <para>
/// The gate always measures from an ANCHOR — the destination's chosen library root, or the one holding
/// the file — and the destination folder handed to it is always relative, because the engine renders
/// only relative folders. A benign subfolder resolves UNDER the anchor and is accepted; a "../.."
/// traversal is REJECTED; an over-FullPathMax target is rejected. The sibling case ("root" vs
/// "rootEvil") proves the prefix check is boundary-aware.
/// </para>
/// <para>
/// A non-empty <c>AllowedRoots</c> NARROWS that area further: the same resolved target must also lie
/// under one of the configured roots. It cannot widen — the anchor check runs first and holds either
/// way — so these cases assert a second refusal on top of the first, never a permission.
/// </para>
/// PURE — no disk.
/// </summary>
/// <remarks>
/// Every case is driven through the one <c>Resolve</c> form the planner calls, so a regression in the
/// branch selection itself — not just in the confinement maths — fails here.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class PathConfinementTests
{
    private const string Root = "media/videos";

    // An absolute allowed root that exists in path-syntax terms on the current OS.
    private static string AllowedRoot => OperatingSystem.IsWindows() ? @"D:\media" : "/srv/media";

    private static IReadOnlyList<string> Roots => [AllowedRoot];

    /// <summary>A Cove library path, absolute as the host's own configuration always is.</summary>
    private static string LibraryPath => OperatingSystem.IsWindows() ? @"E:\library" : "/srv/library";

    /// <summary>The shipped default: no allowlist, so the anchor is the whole of the containment.</summary>
    private static PathConfinement.ConfinementResult ResolveWithNoAllowedRoots(
        string destinationFolder,
        string newBasename,
        RenamerOptions? options = null) =>
        PathConfinement.Resolve(
            allowedRoots: [],
            anchor: Root,
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
    public void Sibling_NotMistakenForChild_Rejected()
    {
        // Resolving "../videosEvil" from "media/videos" lands on a SIBLING "media/videosEvil"
        // whose absolute form shares the textual prefix of the root but is NOT under it.
        var r = ResolveWithNoAllowedRoots(destinationFolder: "../videosEvil", newBasename: "film.mkv");

        Assert.False(r.Accepted);
    }

    /// <summary>
    /// A ROOTED folder template is refused outright, before it is ever combined with the anchor.
    /// </summary>
    /// <remarks>
    /// The engine renders folder templates relative and this branch is therefore unreachable through
    /// it; what it guards is a direct caller of this <c>public</c> member. Without it the answer is not
    /// merely permissive, it is undefined and differs by platform: on POSIX the combine trims the
    /// leading separator, so an absolute-looking template becomes an ordinary subfolder under the
    /// anchor — contained, escaping nothing — while on Windows a drive-qualified one reaches
    /// <c>Path.GetFullPath</c> with an embedded colon and the result was never established. Refusing
    /// makes the Windows question moot, which is cheaper than answering it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RootedFolderTemplates))]
    public void RootedFolderTemplate_Rejected(string destinationFolder)
    {
        var r = ResolveWithNoAllowedRoots(destinationFolder, newBasename: "film.mkv");

        Assert.False(r.Accepted);
        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
        Assert.Contains("not relative", r.Reason);
    }

    /// <summary>
    /// Rooted spellings, gated by what is rooted ON THIS PLATFORM rather than by what looks absolute.
    /// </summary>
    /// <remarks>
    /// A leading separator is rooted everywhere, so that case carries the pin on either runner. A
    /// drive-qualified path is rooted only on Windows — on Linux it is an ordinary relative name that
    /// happens to contain a colon — so asserting a refusal for it there would pin the opposite of the
    /// truth. The gate is the platform, never <c>Path.IsPathRooted</c> itself: computing the expectation
    /// from the predicate under test would make this case agree with the resolver whatever it does.
    /// </remarks>
    public static TheoryData<string> RootedFolderTemplates()
    {
        var cells = new TheoryData<string> { "/etc" };

        if (OperatingSystem.IsWindows())
        {
            cells.Add(@"C:\Windows");
            cells.Add(@"\\server\share");
        }

        return cells;
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

    // ── AllowedRoots as a NARROWING: the same target must also sit under a configured root ─────────

    [Fact]
    public void RelativeTarget_ResolvesUnderSource_AcceptedOnlyWhenUnderARoot()
    {
        // A relative destination is resolved under the anchor; it is accepted only when that resolved
        // path lands under a configured root. Here the anchor IS the root, so it passes.
        var r = PathConfinement.Resolve(
            Roots, anchor: AllowedRoot.Replace('\\', '/'), destinationFolder: "Acme/2024",
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
            Roots, anchor: "media/videos", destinationFolder: "Acme/2024",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.False(r.Accepted);
        Assert.Contains("not under any allowed root", r.Reason);
    }

    [Fact]
    public void OverFullPathMax_UnderAllowedRoot_Rejected()
    {
        // Anchored ON the allowed root, so the narrowing accepts and the length refusal is what is left
        // to observe. Why the two stay distinct: PathConfinement.ConfinementRejection.
        var opts = new RenamerOptions { FullPathMax = 40 };

        var r = PathConfinement.Resolve(
            Roots, anchor: AllowedRoot.Replace('\\', '/'), destinationFolder: "Acme",
            newBasename: new string('x', 300) + ".mkv", options: opts);

        Assert.False(r.Accepted);
        Assert.Contains("FullPathMax", r.Reason);
    }

    // The no-allowlist fallback, asserted through the allowlist-shaped call: an empty list must narrow
    // nothing, so this restates an expectation an earlier case also holds — deliberately, since what it
    // pins is that the two spellings reach the SAME decision, which neither can show alone.
    [Fact]
    public void EmptyRoots_BenignRelativeSubfolder_ResolvesUnderSource_Accepted()
    {
        var r = PathConfinement.Resolve(
            allowedRoots: [], anchor: "media/videos", destinationFolder: "Acme/2024",
            newBasename: "film.mkv", options: new RenamerOptions());

        Assert.True(r.Accepted);
        Assert.EndsWith("/media/videos/Acme/2024", r.TargetFolderPath);
    }

    // ── ContainingRoot: the anchor picker, which shares IsUnderRoot with the gate above ────────────
    //
    // The boundary moved here with the design. A rooted destination used to be the reachable way for a
    // prefix match to become a permission; there is no rooted destination now, and the reachable way is
    // a FILE under "…/libraryEvil" while "…/library" is the configured library path. Deleting the old
    // rooted case without pinning this one would leave that shared predicate's boundary unheld at the
    // only site that can still meet it.

    [Fact]
    public void ContainingRoot_SiblingOfALibraryPath_IsNotContained()
    {
        var sibling = OperatingSystem.IsWindows() ? "E:/libraryEvil/loot" : "/srv/libraryEvil/loot";

        Assert.Null(PathConfinement.ContainingRoot(sibling, [LibraryPath]));
    }

    [Fact]
    public void ContainingRoot_WhenLibraryPathsNest_TheLongestWins()
    {
        // The nearer boundary is the one the user drew around the file, so a library declaring both a
        // tree and a subtree of it anchors on the subtree.
        string outer = OperatingSystem.IsWindows() ? "E:/library" : "/srv/library";
        string inner = outer + "/video";

        Assert.Equal(inner, PathConfinement.ContainingRoot(inner + "/2024/clip.mkv", [outer, inner]));
    }
}
