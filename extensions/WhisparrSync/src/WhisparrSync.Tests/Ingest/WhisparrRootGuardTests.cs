using WhisparrSync.Ingest;

namespace WhisparrSync.Tests.Ingest;

/// <summary>
/// The defense-in-depth path-containment guard: an imported path is accepted only when it
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

    [Fact]
    public void SymlinkInsideRoot_WhoseTargetEscapes_IsNotWithin()
    {
        // WR-02: a symlink INSIDE a root pointing OUT of it (e.g. /root/link -> /outside) must not let a path
        // through it (/root/link/secret.txt) pass containment — Path.GetFullPath is lexical and would wrongly
        // accept it; real-path resolution follows the link to /outside and rejects it.
        var baseDir = Path.Combine(Path.GetTempPath(), "wsg-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(baseDir, "root");
        var outside = Path.Combine(baseDir, "outside");
        var link = Path.Combine(root, "link");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            Directory.CreateSymbolicLink(link, outside);

            var throughSymlink = Path.Combine(link, "secret.txt"); // /root/link/secret.txt → really /outside/secret.txt

            Assert.False(WhisparrRootGuard.IsWithinAnyRoot(throughSymlink, [root]));
            // A real (non-symlinked) file directly inside the root is still accepted.
            var realInside = Path.Combine(root, "ok.txt");
            File.WriteAllText(realInside, "ok");
            Assert.True(WhisparrRootGuard.IsWithinAnyRoot(realInside, [root]));
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
    }
}
