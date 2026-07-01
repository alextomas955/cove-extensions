using System.Diagnostics;
using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The load-bearing GATE-03 proof: a junction physically INSIDE an allowed root but pointing OUTSIDE
/// it is REJECTED by <see cref="CanonicalPathGuard"/> at the write boundary. Junctions require no
/// privilege (created via <c>cmd /c mklink /J</c>), so this is a MANDATORY, non-skippable
/// <see cref="FactAttribute"/> — it always runs on the dev box and any CI. Also proves a benign
/// (no-link) destination under the root is ACCEPTED and that a resolution error fails CLOSED (reject).
/// Exercised against the real filesystem via the <see cref="TempDir"/> fixture.
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Adversarial", "Junction")]
public sealed class CanonicalGuardJunctionTests
{
    /// <summary>Creates an NTFS junction <paramref name="link"/> → <paramref name="target"/> via <c>cmd /c mklink /J</c> (no privilege required).</summary>
    private static void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(5000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("mklink /J failed: " + p.StandardError.ReadToEnd());
        }
    }

    [Fact] // NOT skippable — junctions always work; this IS the GATE-03 proof.
    public void JunctionInsideAllowedRoot_PointingOutside_IsRejected()
    {
        using var dir = new TempDir();
        string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")).FullName;
        string escape = Path.Combine(allowed, "escape"); // lives inside the allowed root…
        MakeJunction(escape, outside);                    // …but resolves outside it.

        var r = CanonicalPathGuard.Check((escape + "/file.mkv").Replace('\\', '/'), [allowed]);

        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
        Assert.Contains("outside every allowed root", r.Reason);
    }

    [Fact]
    public void BenignSubdirectoryUnderAllowedRoot_NoLink_IsAccepted()
    {
        using var dir = new TempDir();
        string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
        // A real subdirectory physically under the allowed root (no reparse point).
        string sub = Directory.CreateDirectory(Path.Combine(allowed, "season-01")).FullName;

        var r = CanonicalPathGuard.Check((sub + "/file.mkv").Replace('\\', '/'), [allowed]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
    }

    [Fact] // WR-01: an allowlisted root that is itself a junction must still accept its children.
    public void AllowedRootIsJunction_ChildDestination_IsAccepted()
    {
        // The allowed root is a JUNCTION to a real directory on (logically) another location — the
        // "library relocated onto another volume" pattern. The target resolves (link side) to the
        // real backing dir; the root must canonicalize the SAME way (link-resolved) or a perfectly
        // legitimate child would be spuriously rejected. This locks the WR-01 fix to CanonicalRoot.
        using var dir = new TempDir();
        string real = Directory.CreateDirectory(Path.Combine(dir.Root, "realmedia")).FullName;
        string rootLink = Path.Combine(dir.Root, "media"); // allowlisted root, but a junction…
        MakeJunction(rootLink, real);                       // …pointing at the real backing dir.

        // The destination subdir does NOT exist yet (the normal pre-move case): the resolver climbs
        // to the deepest EXISTING ancestor — the junction root itself — and link-resolves it to the
        // real backing dir, so the target resolves to <real>/season-01/file.mkv. The allowlisted root
        // must canonicalize the SAME way (link-resolved to <real>) or this legitimate child is
        // spuriously rejected. This locks the WR-01 fix to CanonicalRoot.
        var r = CanonicalPathGuard.Check(
            (Path.Combine(rootLink, "season-01") + "/file.mkv").Replace('\\', '/'),
            [rootLink.Replace('\\', '/')]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void UnresolvableAncestor_FailsClosed_Rejected()
    {
        // A destination whose ancestor chain cannot be resolved to a real on-disk location (here an
        // unreachable UNC share that the owner DID allowlist, so it gets past the syntax gate) must
        // REJECT, never accept — fail-closed. The resolution returns no real target, so containment
        // can't be proven and the guard refuses rather than guessing benign.
        string target = @"\\renamer-no-such-host\share\media\out".Replace('\\', '/');

        var r = CanonicalPathGuard.Check(target, [@"\\renamer-no-such-host\share"]);

        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
    }
}
