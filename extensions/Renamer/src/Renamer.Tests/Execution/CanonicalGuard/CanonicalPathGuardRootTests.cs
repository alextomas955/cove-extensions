using System.Diagnostics;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
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

        // The returned bool is what makes a timeout its own named failure. Discarded, the next line
        // reads ExitCode on a process that is still running, which throws about the accessor
        // ("Process must exit before …") rather than about mklink — so the arrangement failure is
        // reported as something else entirely, and the child is left behind. Both streams are drained
        // only after a confirmed exit: mklink emits one short line, well inside the pipe buffer, so
        // reading late cannot deadlock THIS child, but it would for a chattier one.
        if (!p.WaitForExit(5000))
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"mklink /J did not exit within 5s for '{link}' -> '{target}'.");
        }

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mklink /J failed ({p.ExitCode}) for '{link}' -> '{target}': "
                    + p.StandardError.ReadToEnd()
                    + p.StandardOutput.ReadToEnd());
        }
    }

    [Fact] // The defect's exact shape: allowlist a volume, route into a folder that does not exist yet.
    public void NonExistentFolderUnderVolumeRoot_IsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs a Windows drive root (subst)");

        using var drive = new SubstDrive();
        string rootFwd = drive.Root.Replace('\\', '/'); // e.g. "Z:/"

        var r = CanonicalPathGuard.Check(rootFwd + "Films", [rootFwd]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
        Assert.Equal(rootFwd + "Films", r.ResolvedTarget, ignoreCase: true);
    }

    [Fact]
    public void VolumeRootItselfAsDestination_IsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs a Windows drive root (subst)");

        using var drive = new SubstDrive();
        string rootFwd = drive.Root.Replace('\\', '/');

        var r = CanonicalPathGuard.Check(rootFwd, [rootFwd]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
        Assert.Equal(rootFwd, r.ResolvedTarget, ignoreCase: true);
    }

    [Fact] // A REAL volume, not a subst mapping — the defect was never subst-specific.
    public void NonExistentFolderUnderRealVolumeRoot_IsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs a Windows drive root");

        // Read-only: the guard resolves and compares paths, it never creates anything, so naming a
        // folder that does not exist at a real volume root touches nothing on that volume.
        string rootFwd = Path.GetPathRoot(Path.GetTempPath())!.Replace('\\', '/');
        string target = rootFwd + "renamer-no-such-destination";

        var r = CanonicalPathGuard.Check(target, [rootFwd]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
        Assert.Equal(target, r.ResolvedTarget, ignoreCase: true);
    }

    [Fact] // The already-working half: an EXISTING folder under a volume root must decide exactly as before.
    public void ExistingFolderUnderVolumeRoot_IsStillAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs a Windows drive root (subst)");

        using var drive = new SubstDrive();
        string rootFwd = drive.Root.Replace('\\', '/');
        string existing = Directory.CreateDirectory(Path.Combine(drive.Root, "Films")).FullName;

        var r = CanonicalPathGuard.Check(existing.Replace('\\', '/'), [rootFwd]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
        Assert.Equal(rootFwd + "Films", r.ResolvedTarget, ignoreCase: true);
    }

    [Fact] // Confinement must survive the relaxation: skipping link resolution on the ROOT never skips it below.
    public void JunctionUnderVolumeRoot_EscapingAllowedRoots_IsStillRejected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs an NTFS junction (cmd /c mklink /J)");

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

    [Fact] // A drive-root SHAPE is not enough: the root must actually exist, or the guard fails closed.
    public void UnmappedVolumeRoot_IsStillRejectedFailClosed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs Windows drive-letter semantics");

        char? free = FirstUnmappedDriveLetter();
        Assert.SkipWhen(free is null, "no unmapped drive letter available on this host");

        string rootFwd = free.Value + ":/";

        var r = CanonicalPathGuard.Check(rootFwd + "Films", [rootFwd]);

        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
        Assert.Contains("canonical resolution failed", r.Reason);
    }

    // The path below is a UNC share on Windows and an ordinary non-existent POSIX path on Linux, so the
    // guard refuses on BOTH — for different stated reasons. The refusal is the property worth holding
    // everywhere and is asserted unconditionally; only the wording is platform-specific, and it gets its
    // own gated case rather than being skipped wholesale. Skipping the pair would delete Linux coverage
    // of a fail-closed guard, which is the species of hole this suite exists to close.
    private const string UnreachableUncTarget = @"\\renamer-no-such-host\share\media\out";
    private const string UnreachableUncRoot = @"\\renamer-no-such-host\share";

    [Fact] // The property the fix must not trade away: an ancestor that cannot be resolved is a REJECT.
    public void UnresolvableAncestor_IsStillRejectedFailClosed()
    {
        // The owner allowlisted this UNC root, so it clears the syntax gate and reaches resolution —
        // where containment cannot be proven and the guard refuses.
        var r = CanonicalPathGuard.Check(UnreachableUncTarget.Replace('\\', '/'), [UnreachableUncRoot]);

        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
    }

    [Fact] // The Windows WORDING of the same refusal: an unreachable host is a canonical-resolution failure.
    public void UnresolvableAncestor_WindowsReasonNamesCanonicalFailure()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "asserts the Windows UNC reason string; on Unix the same path is an ordinary missing directory and the guard refuses on containment instead"
        );

        var r = CanonicalPathGuard.Check(UnreachableUncTarget.Replace('\\', '/'), [UnreachableUncRoot]);

        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
        Assert.Contains("canonical resolution failed", r.Reason);
    }

    [Fact] // The whole pipeline, not the guard alone — the unit cases above cannot see a refusal reintroduced downstream.
    public async Task ExecutorRun_AllowlistCoveringVolumeRoot_MovesIntoNonExistentFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs a Windows drive root (subst)");

        using var dir = new TempDir();
        using var drive = new SubstDrive();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The allowlist is the ONLY variable that used to decide this run: with AllowedRoots empty
            // the guard is inert (RenamerExecutor gates on Count > 0) and the file landed; with the
            // volume root allowlisted the same run left the source in place and moved nothing. Both
            // halves were measured, which is what makes the guard — not the mover, not the OS — the
            // component this case holds to account.
            string srcDir = Directory.CreateDirectory(Path.Combine(dir.Root, "source")).FullName;
            string rootFwd = drive.Root.Replace('\\', '/');
            string targetFwd = rootFwd + "Films"; // does not exist yet: its deepest existing ancestor IS the volume root

            string srcFolderPath = srcDir.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolderPath, "clip.mkv", "My Film");

            string oldFull = Path.Combine(srcDir, "clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolderPath + "/clip.mkv", targetFwd + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", targetFwd),
            ]);

            var executor = new RenamerExecutor(
                new CoveRenamerDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test", new DiskMover());
            var options = new RenamerOptions { AllowedRoots = [rootFwd] };

            var result = await executor.ExecuteAsync(plan, options, default);

            // The typed outcome, not merely a file somewhere: a blocked run reports SkipBlocked here.
            var moved = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Move, moved.Status);
            Assert.Equal(targetFwd + "/My Film.mkv", moved.NewPath, ignoreCase: true);
            Assert.Empty(result.Skipped);
            Assert.Empty(result.Failed);

            string newFull = Path.Combine(drive.Root, "Films", "My Film.mkv");
            Assert.True(File.Exists(newFull), "the allowlisted volume-root destination must land on disk");
            Assert.False(File.Exists(oldFull), "the source must be gone after a successful move");
            Assert.Equal("video-bytes", File.ReadAllText(newFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
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
