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
    public void CaseVariantOfRoot_IsNotWithin_CaseSensitive()
        // WR-01: containment is case-SENSITIVE. On the Linux/Docker target /data/Media is a different directory
        // than the allow-listed /data/media, so a differently-cased path must NOT match (case-folding would let
        // a path resolve into a root the admin never allow-listed — a security weakening).
        => Assert.False(WhisparrRootGuard.IsWithinAnyRoot("/data/Media/Scene/SCENE.MKV", Roots));

    [Fact]
    public void ExactCasePathInsideRoot_IsWithin()
        // The same path with the root's exact casing is still contained — only the case MISMATCH is rejected.
        => Assert.True(WhisparrRootGuard.IsWithinAnyRoot("/data/media/Scene/SCENE.MKV", Roots));

    [Fact]
    public void MatchesAnyOfMultipleRoots()
        => Assert.True(WhisparrRootGuard.IsWithinAnyRoot(
            "/mnt/library/a.mp4", ["/data/media", "/mnt/library"]));
}
