using WhisparrSync.Ingest;

namespace WhisparrSync.Tests;

/// <summary>
/// The defense-in-depth path-containment guard (T-03-PT): an imported path is accepted only when it
/// canonicalizes inside a known Whisparr root at a SEGMENT BOUNDARY, and an empty root set is rejected
/// fail-closed. Pure and host-free.
/// </summary>
public sealed class WhisparrRootGuardTests
{
    private static readonly string[] Roots = ["/data/media"];

    [Fact]
    public void PathInsideRoot_IsWithin()
        => Assert.True(WhisparrRootGuard.IsWithinAnyRoot("/data/media/Scene/Scene.mkv", Roots));

    [Fact]
    public void PathEqualToRoot_IsWithin()
        => Assert.True(WhisparrRootGuard.IsWithinAnyRoot("/data/media", Roots));

    [Fact]
    public void PathOutsideRoot_IsNotWithin()
        => Assert.False(WhisparrRootGuard.IsWithinAnyRoot("/etc/passwd", Roots));

    [Fact]
    public void SiblingPrefixRoot_IsNotWithin()
        => Assert.False(WhisparrRootGuard.IsWithinAnyRoot("/data/media-evil/x.mkv", Roots));

    [Fact]
    public void EmptyRootSet_IsNotWithin_FailClosed()
        => Assert.False(WhisparrRootGuard.IsWithinAnyRoot("/data/media/x.mkv", []));

    [Fact]
    public void CaseVariants_AreContained()
        // NormalizePath case-folds both sides, so a differently-cased path still resolves inside the root.
        // (Separator unification also happens, but Path.GetFullPath only treats '\' as a separator on Windows,
        // so a cross-OS backslash path is not portably assertable here.)
        => Assert.True(WhisparrRootGuard.IsWithinAnyRoot("/data/Media/Scene/SCENE.MKV", Roots));

    [Fact]
    public void MatchesAnyOfMultipleRoots()
        => Assert.True(WhisparrRootGuard.IsWithinAnyRoot(
            "/mnt/library/a.mp4", ["/data/media", "/mnt/library"]));
}
