using System.Diagnostics;
using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.CanonicalGuard;

/// <summary>
/// Pins WHICH component decides a destination whose deepest EXISTING ancestor is a volume root, and
/// what it decides. The deciding component is <see cref="CanonicalPathGuard"/> — every case here
/// asserts its returned decision and reason rather than whether a file happened to land, because a
/// landed-file assertion passes or fails for reasons in four other layers.
///
/// <para>
/// The regression this locks: <c>Directory.ResolveLinkTarget(path, returnFinalTarget: true)</c> throws
/// <c>DirectoryNotFoundException</c> for EVERY Windows drive root while <c>Directory.Exists</c> returns
/// true for the same path — measured on a <c>subst</c> mapping and on two real volumes. That exception
/// derives from <see cref="IOException"/>, so the guard's fail-closed catch turned an ordinary
/// configuration — allowlist a volume, route into a folder that does not exist yet — into a silent
/// <c>SkipBlocked</c> for every destination whose deepest existing ancestor was a volume root. The OS,
/// <c>CrossVolumeMover</c> and the pure <c>PathConfinement</c> gate were each measured to accept the same
/// shape, which is why the refusal went unnamed for a phase.
/// </para>
///
/// <para>
/// The fail-closed property the guard's own doc promises is asserted in the same suite, so the fix
/// cannot regress into a wider exception filter: an unresolvable ancestor and an unmapped volume root
/// both still reject, and a junction under a volume root that escapes every allowed root still rejects
/// on containment.
/// </para>
/// </summary>
[Trait("Tier", "L1")]
public sealed class CanonicalPathGuardRootTests
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

    [SkippableFact] // The defect's exact shape: allowlist a volume, route into a folder that does not exist yet.
    public void NonExistentFolderUnderVolumeRoot_IsAccepted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs a Windows drive root (subst)");

        using var drive = new SubstDrive();
        string rootFwd = drive.Root.Replace('\\', '/'); // e.g. "Z:/"

        var r = CanonicalPathGuard.Check(rootFwd + "Films", [rootFwd]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
        Assert.Equal(rootFwd + "Films", r.ResolvedTarget, ignoreCase: true);
    }

    [SkippableFact]
    public void VolumeRootItselfAsDestination_IsAccepted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs a Windows drive root (subst)");

        using var drive = new SubstDrive();
        string rootFwd = drive.Root.Replace('\\', '/');

        var r = CanonicalPathGuard.Check(rootFwd, [rootFwd]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
        Assert.Equal(rootFwd, r.ResolvedTarget, ignoreCase: true);
    }

    [SkippableFact] // A REAL volume, not a subst mapping — the defect was never subst-specific.
    public void NonExistentFolderUnderRealVolumeRoot_IsAccepted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs a Windows drive root");

        // Read-only: the guard resolves and compares paths, it never creates anything, so naming a
        // folder that does not exist at a real volume root touches nothing on that volume.
        string rootFwd = Path.GetPathRoot(Path.GetTempPath())!.Replace('\\', '/');
        string target = rootFwd + "renamer-no-such-destination";

        var r = CanonicalPathGuard.Check(target, [rootFwd]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
        Assert.Equal(target, r.ResolvedTarget, ignoreCase: true);
    }

    [SkippableFact] // The already-working half: an EXISTING folder under a volume root must decide exactly as before.
    public void ExistingFolderUnderVolumeRoot_IsStillAccepted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs a Windows drive root (subst)");

        using var drive = new SubstDrive();
        string rootFwd = drive.Root.Replace('\\', '/');
        string existing = Directory.CreateDirectory(Path.Combine(drive.Root, "Films")).FullName;

        var r = CanonicalPathGuard.Check(existing.Replace('\\', '/'), [rootFwd]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
        Assert.Equal(rootFwd + "Films", r.ResolvedTarget, ignoreCase: true);
    }

    [SkippableFact] // Confinement must survive the relaxation: skipping link resolution on the ROOT never skips it below.
    public void JunctionUnderVolumeRoot_EscapingAllowedRoots_IsStillRejected()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs an NTFS junction (cmd /c mklink /J)");

        using var dir = new TempDir();
        using var drive = new SubstDrive();
        string rootFwd = drive.Root.Replace('\\', '/');
        string outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")).FullName;

        string escape = Path.Combine(drive.Root, "escape"); // one level under the allowlisted volume root…
        MakeJunction(escape, outside);                       // …but resolving off the volume entirely.

        var r = CanonicalPathGuard.Check(escape.Replace('\\', '/') + "/Films", [rootFwd]);

        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
        Assert.Contains("outside every allowed root", r.Reason);
    }

    [SkippableFact] // A drive-root SHAPE is not enough: the root must actually exist, or the guard fails closed.
    public void UnmappedVolumeRoot_IsStillRejectedFailClosed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs Windows drive-letter semantics");

        char? free = FirstUnmappedDriveLetter();
        Skip.If(free is null, "no unmapped drive letter available on this host");

        string rootFwd = free!.Value + ":/";

        var r = CanonicalPathGuard.Check(rootFwd + "Films", [rootFwd]);

        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
        Assert.Contains("canonical resolution failed", r.Reason);
    }

    [Fact] // The property the fix must not trade away: an ancestor that cannot be resolved is a REJECT.
    public void UnresolvableAncestor_IsStillRejectedFailClosed()
    {
        // The owner allowlisted this UNC root, so it clears the syntax gate and reaches resolution —
        // where the host is unreachable, containment cannot be proven, and the guard refuses.
        string target = @"\\renamer-no-such-host\share\media\out".Replace('\\', '/');

        var r = CanonicalPathGuard.Check(target, [@"\\renamer-no-such-host\share"]);

        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
        Assert.Contains("canonical resolution failed", r.Reason);
    }

    /// <summary>A drive letter with no volume mapped to it, or null when every letter is in use.</summary>
    private static char? FirstUnmappedDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'D'; c--)
        {
            if (!used.Contains(c))
            {
                return c;
            }
        }

        return null;
    }
}
