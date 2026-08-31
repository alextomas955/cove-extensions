using WhisparrSync.Import;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// The arithmetic that turns a path one system reported into the paths the other might really hold
/// it at, and the two rules that keep a reported string from reaching the filesystem.
/// </summary>
/// <remarks>
/// Pure throughout, so every branch is reachable here. The container proves the one case that needs
/// a real file; not-found, ambiguous and every refusal are proven exhaustively in this file, which is
/// the split the two tiers exist for.
/// </remarks>
public sealed class PathCandidateGuardTests
{
    /// <summary>The host's own library paths in the containerized fixture, transcribed by hand.</summary>
    /// <remarks>
    /// Written out rather than read from the compose file, because an expectation computed from the
    /// thing it checks agrees with it whatever either says.
    /// </remarks>
    private static readonly string[] HostRoots = ["/data", "/data2"];

    [Fact]
    public void OneTailUnderEachHostRootIsACandidate()
    {
        var reading = PathCandidateGuard.Read(
            "/media/scenes/studio/scene.mp4", ["/media"], HostRoots);

        Assert.Null(reading.Refusal);
        Assert.Equal(["scenes/studio/scene.mp4"], reading.Tails);
        Assert.Equal(
            ["/data/scenes/studio/scene.mp4", "/data2/scenes/studio/scene.mp4"],
            reading.Candidates);
    }

    /// <summary>
    /// Every reporting root that contains the path yields its own tail, and none is chosen here.
    /// </summary>
    /// <remarks>
    /// Nested roots are a real configuration. Deciding between them in arithmetic would be a guess
    /// the caller could not see; leaving both means what is on disk settles it.
    /// </remarks>
    [Fact]
    public void NestedReportingRootsEachYieldTheirOwnTail()
    {
        var reading = PathCandidateGuard.Read(
            "/media/inner/scene.mp4", ["/media", "/media/inner"], ["/data"]);

        Assert.Null(reading.Refusal);
        Assert.Equal(["inner/scene.mp4", "scene.mp4"], reading.Tails);
        Assert.Equal(["/data/inner/scene.mp4", "/data/scene.mp4"], reading.Candidates);
    }

    /// <summary>A root does not contain a sibling whose name merely extends its own.</summary>
    [Fact]
    public void ASiblingRootWhoseNameExtendsAnothersDoesNotContainIt()
    {
        Assert.Equal(
            PathCandidateRefusal.PathOutsideEveryReportedRoot,
            PathCandidateGuard.Read("/media22/scene.mp4", ["/media"], HostRoots).Refusal);

        // The same rule on the host side: a tail placed under /data must not be taken to sit under
        // /data2 or the other way about.
        var reading = PathCandidateGuard.Read("/media/scene.mp4", ["/media"], ["/data"]);
        Assert.Equal(["/data/scene.mp4"], reading.Candidates);
    }

    /// <summary>
    /// A parent-directory segment in the reported tail cannot produce a candidate outside its root.
    /// </summary>
    /// <remarks>
    /// The collapse happens after the join, so the containment check has to happen after the
    /// collapse. A check made before it would see a string that still begins with the root.
    /// </remarks>
    [Theory]
    [InlineData("/media/../../etc/shadow")]
    [InlineData("/media/scenes/../../../etc/shadow")]
    [InlineData("/media/./../etc/shadow")]
    public void ATraversalSegmentInTheTailProducesNoCandidateOutsideItsRoot(string reportedPath)
    {
        var reading = PathCandidateGuard.Read(reportedPath, ["/media"], HostRoots);

        Assert.All(
            reading.Candidates,
            candidate => Assert.True(
                HostRoots.Any(root => candidate.StartsWith(root + "/", StringComparison.Ordinal)),
                $"{candidate} was produced and lies under none of {string.Join(", ", HostRoots)}"));
        Assert.Equal(PathCandidateRefusal.EveryCandidateEscapedItsRoot, reading.Refusal);
        Assert.Empty(reading.Candidates);
    }

    /// <summary>
    /// A parent segment that stays inside its root is kept, collapsed.
    /// </summary>
    /// <remarks>
    /// The discriminating control for the case above: without it, refusing every path carrying a
    /// parent segment would pass it too, and the rule would be "no dots" rather than "no escape".
    /// </remarks>
    [Fact]
    public void AParentSegmentThatStaysInsideItsRootIsKept()
    {
        var reading = PathCandidateGuard.Read(
            "/media/scenes/other/../studio/scene.mp4", ["/media"], ["/data"]);

        Assert.Null(reading.Refusal);
        Assert.Equal(["/data/scenes/studio/scene.mp4"], reading.Candidates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ADeliveryNamingNoPathIsItsOwnRefusal(string? reportedPath)
        => Assert.Equal(
            PathCandidateRefusal.NoReportedPath,
            PathCandidateGuard.Read(reportedPath, ["/media"], HostRoots).Refusal);

    [Fact]
    public void AnInstanceDeclaringNoRootIsItsOwnRefusal()
        => Assert.Equal(
            PathCandidateRefusal.NoReportedRoots,
            PathCandidateGuard.Read("/media/scene.mp4", [], HostRoots).Refusal);

    [Fact]
    public void AHostDeclaringNoLibraryPathIsItsOwnRefusal()
    {
        var reading = PathCandidateGuard.Read("/media/scene.mp4", ["/media"], []);

        Assert.Equal(PathCandidateRefusal.NoLibraryRoots, reading.Refusal);
        // The tail is still reported: it says the reported path WAS resolvable against the instance,
        // which is a different fact from the host having nowhere to put it.
        Assert.Equal(["scene.mp4"], reading.Tails);
    }

    /// <summary>A blank entry in either list is not a root that contains everything.</summary>
    [Fact]
    public void ABlankEntryIsNotARoot()
    {
        Assert.Equal(
            PathCandidateRefusal.NoReportedRoots,
            PathCandidateGuard.Read("/media/scene.mp4", ["", "   "], HostRoots).Refusal);
        Assert.Equal(
            PathCandidateRefusal.NoLibraryRoots,
            PathCandidateGuard.Read("/media/scene.mp4", ["/media"], ["  "]).Refusal);
    }

    /// <summary>A Windows-spelled report resolves against a Linux-spelled host root.</summary>
    /// <remarks>
    /// The two systems need not run on the same platform, and the whole reason this arithmetic
    /// exists is that they need not spell one file the same way.
    /// </remarks>
    [Fact]
    public void ABackslashSpelledReportResolvesAgainstAForwardSlashRoot()
    {
        var reading = PathCandidateGuard.Read(
            @"C:\Media\scenes\scene.mp4", [@"C:\media"], ["/data"]);

        Assert.Null(reading.Refusal);
        Assert.Equal(["/data/scenes/scene.mp4"], reading.Candidates);
    }

    [Fact]
    public void ExactlyOneVerifiedCandidateIsTheOneToActOn()
    {
        Assert.Equal("/data/scene.mp4", PathCandidateGuard.SingleVerified(["/data/scene.mp4"]));
        Assert.Null(PathCandidateGuard.SingleVerified([]));
        Assert.Null(PathCandidateGuard.SingleVerified(["/data/scene.mp4", "/data2/scene.mp4"]));
    }
}
