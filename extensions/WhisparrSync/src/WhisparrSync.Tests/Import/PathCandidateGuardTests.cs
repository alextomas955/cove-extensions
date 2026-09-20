using WhisparrSync.Import;

namespace WhisparrSync.Tests.Import;

// Pure throughout, so every branch is reachable here. The container proves the one case that needs
// a real file; not-found, ambiguous and every refusal are proven exhaustively in this file.
public sealed class PathCandidateGuardTests
{
    // The host's own library paths in the containerized fixture, transcribed by hand. Written out
    // rather than read from the compose file, because an expectation computed from the thing it checks
    // agrees with it whatever either says.
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

    // Nested roots are a real configuration. Deciding between them in arithmetic would be a guess the
    // caller could not see; leaving both means what is on disk settles it.
    [Fact]
    public void NestedReportingRootsEachYieldTheirOwnTail()
    {
        var reading = PathCandidateGuard.Read(
            "/media/inner/scene.mp4", ["/media", "/media/inner"], ["/data"]);

        Assert.Null(reading.Refusal);
        Assert.Equal(["inner/scene.mp4", "scene.mp4"], reading.Tails);
        Assert.Equal(["/data/inner/scene.mp4", "/data/scene.mp4"], reading.Candidates);
    }

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

    // The collapse happens after the join, so the containment check has to happen after the collapse. A
    // check made before it would see a string that still begins with the root.
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

    // The control for the case above: without it, refusing every path carrying a parent segment would
    // pass it too, and the rule would be "no dots" rather than "no escape".
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
        // The tail is still reported: it says the reported path was resolvable against the instance,
        // which is a different fact from the host having nowhere to put it.
        Assert.Equal(["scene.mp4"], reading.Tails);
    }

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

    // The two systems need not run on the same platform, and this arithmetic exists because they need
    // not spell one file the same way.
    [Fact]
    public void ABackslashSpelledReportResolvesAgainstAForwardSlashRoot()
    {
        var reading = PathCandidateGuard.Read(
            @"C:\Media\scenes\scene.mp4", [@"C:\media"], ["/data"]);

        Assert.Null(reading.Refusal);
        Assert.Equal(["/data/scenes/scene.mp4"], reading.Candidates);
    }

    // The line a refusal is counted under is blank here, which is the case the banner most needs to
    // show: the delivery named a path its own instance cannot place.
    [Fact]
    public void APathUnderNoReportingRootIsCountedUnderNoRoot()
    {
        var reading = PathCandidateGuard.Read("/elsewhere/scene.mp4", ["/media"], HostRoots);

        Assert.Empty(reading.ReportingRoots);
        Assert.Equal("", reading.RefusalRoot);
    }

    [Fact]
    public void TheContainingReportingRootCarriesTheLine()
    {
        var reading = PathCandidateGuard.Read("/media/scenes/scene.mp4", ["/media"], HostRoots);

        Assert.Equal(["/media"], reading.ReportingRoots);
        Assert.Equal("/media", reading.RefusalRoot);
    }

    // Both contain the path and both yield a tail. The choice groups a count and settles nothing about
    // which file to import.
    [Fact]
    public void NestedReportingRootsCountUnderTheFirstTheInstanceListed()
    {
        var reading = PathCandidateGuard.Read(
            "/media/inner/scene.mp4", ["/media/inner", "/media"], ["/data"]);

        Assert.Equal(["/media/inner", "/media"], reading.ReportingRoots);
        Assert.Equal("/media/inner", reading.RefusalRoot);
    }

    // Transcribed by hand rather than counted from the enum, so adding a member fails here and has to
    // be decided rather than absorbed.
    [Fact]
    public void TheRefusalVocabularyIsTheThreeCausesAndNoMore()
        => Assert.Equal(
            new[]
            {
                ImportRefusalCause.NotFoundUnderAnyRoot,
                ImportRefusalCause.AmbiguousCandidates,
                ImportRefusalCause.Unreadable,
            }.Order(),
            Enum.GetValues<ImportRefusalCause>().Order());

    [Fact]
    public void AResolutionNamesEitherAPathOrACauseAndNeverBoth()
    {
        var imported = PathCandidateGuard.Resolve([Found("/data/scene.mp4", 10)], 10);
        Assert.Equal("/data/scene.mp4", imported.Path);
        Assert.Null(imported.Cause);

        var refused = PathCandidateGuard.Resolve([], 10);
        Assert.Null(refused.Path);
        Assert.NotNull(refused.Cause);
    }

    [Fact]
    public void ExactlyOneVerifiedCandidateIsTheOneToImport()
    {
        var resolution = PathCandidateGuard.Resolve(
            [Found("/data/scene.mp4", 10), Absent("/data2/scene.mp4")], 10);

        Assert.Equal("/data/scene.mp4", resolution.Path);
        Assert.Null(resolution.Cause);
    }

    [Fact]
    public void NoVerifiedCandidateIsTheNotFoundCause()
    {
        var resolution = PathCandidateGuard.Resolve(
            [Absent("/data/scene.mp4"), Absent("/data2/scene.mp4")], 10);

        Assert.Null(resolution.Path);
        Assert.Equal(ImportRefusalCause.NotFoundUnderAnyRoot, resolution.Cause);
    }

    // The refusal is the point: a longest-root or first-root rule would import one of the two and leave
    // the user no signal that the other exists.
    [Fact]
    public void TwoVerifiedCandidatesUnderNestedCoveRootsAreRefusedAsAmbiguous()
    {
        var reading = PathCandidateGuard.Read(
            "/media/scene.mp4", ["/media"], ["/data", "/data/inner"]);
        Assert.Equal(["/data/scene.mp4", "/data/inner/scene.mp4"], reading.Candidates);

        var resolution = PathCandidateGuard.Resolve(
            [.. reading.Candidates.Select(path => Found(path, 10))], 10);

        Assert.Null(resolution.Path);
        Assert.Equal(ImportRefusalCause.AmbiguousCandidates, resolution.Cause);
    }

    // Its control is the same pair resolved against the size that does match, which imports: without
    // it, a guard that verified nothing at all would pass the refusal too.
    [Fact]
    public void ASizeMismatchTurnsASingleVerificationIntoNone()
    {
        Assert.Equal(
            ImportRefusalCause.NotFoundUnderAnyRoot,
            PathCandidateGuard.Resolve([Found("/data/scene.mp4", 11)], 10).Cause);

        Assert.Equal(
            "/data/scene.mp4",
            PathCandidateGuard.Resolve([Found("/data/scene.mp4", 10)], 10).Path);
    }

    // Absence of a size is not a mismatch. A candidate of any length verifies, and the same candidate
    // absent from disk still does not.
    [Fact]
    public void APayloadCarryingNoSizeVerifiesOnPresenceAlone()
    {
        Assert.Equal(
            "/data/scene.mp4",
            PathCandidateGuard.Resolve([Found("/data/scene.mp4", 999)], null).Path);

        Assert.Equal(
            ImportRefusalCause.NotFoundUnderAnyRoot,
            PathCandidateGuard.Resolve([Absent("/data/scene.mp4")], null).Cause);
    }

    private static ProbedCandidate Found(string path, long size)
        => new(path, new ProbedPath(true, size));

    private static ProbedCandidate Absent(string path) => new(path, new ProbedPath(false, null));
}
