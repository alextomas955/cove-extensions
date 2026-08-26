using System.Diagnostics;
using System.Runtime.InteropServices;
using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.CanonicalGuard;

/// <summary>
/// GATE-03 adversarial containment: every alias shape that can make a destination resolve somewhere
/// other than where it reads — a junction, a directory symlink, an 8.3 short name — is REJECTED by
/// <see cref="CanonicalPathGuard"/> at the write boundary, while a benign child of an allowed root and
/// an allowlisted root that is ITSELF a link are both ACCEPTED. Exercised against the real filesystem
/// via the <see cref="TempDir"/> fixture.
/// </summary>
/// <remarks>
/// Every case here that needs a link or a short name is gated with <c>Assert.SkipUnless</c> on the
/// capability it requires, and the gate is load-bearing rather than cosmetic: a guard test that
/// SKIPS reads exactly like one that passed. The skip census — read from the trx RESULTS with
/// <c>outcome="NotExecuted"</c>, never the <c>Counters</c> attribute — is the only instrument that tells
/// the two apart, so a canonical-guard name appearing in it is a failure, not a note. Junctions need no
/// privilege, so the junction cases always run on Windows and are the non-skippable load-bearing proof
/// of the resolution path; a directory symlink needs Developer Mode or admin, so that case probes by
/// attempting the creation and skips WITH A VISIBLE REASON when privilege is absent — never a silent
/// early return.
/// <para>
/// The syntax-only prefix rejections live in the nested <see cref="PrefixSyntax"/> class rather than
/// here, because they fire before any disk resolution and are therefore pure. Keeping them in their own
/// class is what lets each class carry the one tier trait that is true of it.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class CanonicalPathGuardTests
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

    /// <summary>Attempts to create a directory symbolic link; returns true iff privilege allows it.</summary>
    private static bool TryCreateSymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, char[] lpszShortPath, uint cchBuffer);

    /// <summary>Returns the 8.3 short form of <paramref name="longPath"/>, or null when the volume has no short alias for it.</summary>
    private static string? GetShortPath(string longPath)
    {
        var buffer = new char[short.MaxValue];
        uint len = GetShortPathNameW(longPath, buffer, (uint)buffer.Length);
        return len > 0 && len < buffer.Length ? new string(buffer, 0, (int)len) : null;
    }

    [Fact] // On Windows this always runs — junctions need no privilege; it IS the GATE-03 proof.
    [Trait("Adversarial", "Junction")]
    public void JunctionInsideAllowedRoot_PointingOutside_IsRejected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs an NTFS junction (cmd /c mklink /J)");

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

    /// <summary>
    /// The symlink variant of the case above: the same <c>ResolveLinkTarget</c> chain the junction case
    /// proves also resolves directory symlinks.
    /// </summary>
    [Fact]
    [Trait("Adversarial", "Symlink")]
    public void SymlinkInsideAllowedRoot_PointingOutside_IsRejected()
    {
        using var dir = new TempDir();
        string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")).FullName;
        string escape = Path.Combine(allowed, "escape");

        bool created = TryCreateSymlink(escape, outside);
        Assert.SkipUnless(created, "symlink creation requires Developer Mode/admin privilege on this host");

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

    [Fact] // An allowlisted root that is itself a junction must still accept its children.
    [Trait("Adversarial", "Junction")]
    public void AllowedRootIsJunction_ChildDestination_IsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "needs an NTFS junction (cmd /c mklink /J)");

        // The allowed root is a JUNCTION to a real directory on (logically) another location — the
        // "library relocated onto another volume" pattern. The target resolves (link side) to the
        // real backing dir; the root must canonicalize the SAME way (link-resolved) or a perfectly
        // legitimate child would be spuriously rejected. This locks canonicalization to CanonicalRoot.
        using var dir = new TempDir();
        string real = Directory.CreateDirectory(Path.Combine(dir.Root, "realmedia")).FullName;
        string rootLink = Path.Combine(dir.Root, "media"); // allowlisted root, but a junction…
        MakeJunction(rootLink, real);                       // …pointing at the real backing dir.

        // The destination subdir does NOT exist yet (the normal pre-move case): the resolver climbs
        // to the deepest EXISTING ancestor — the junction root itself — and link-resolves it to the
        // real backing dir, so the target resolves to <real>/season-01/file.mkv. The allowlisted root
        // must canonicalize the SAME way (link-resolved to <real>) or this legitimate child is
        // spuriously rejected. This locks canonicalization to CanonicalRoot.
        var r = CanonicalPathGuard.Check(
            (Path.Combine(rootLink, "season-01") + "/file.mkv").Replace('\\', '/'),
            [rootLink.Replace('\\', '/')]);

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Reason);
    }

    /// <summary>
    /// 8.3 short-name aliasing: a destination expressed through a short name (e.g. <c>PROGRA~1</c>) must
    /// resolve to its canonical LONG form before the containment compare, so a short-name path that is
    /// genuinely under the allowed root is treated consistently with its long form (and a short-name path
    /// keyed to dodge a long-form allowlist cannot slip through). <see cref="Path.GetFullPath(string)"/>
    /// does NOT expand short names — only the guard's <c>kernel32!GetLongPathNameW</c> step does.
    /// </summary>
    [Fact]
    [Trait("Adversarial", "ShortName")]
    public void ShortNameDestinationUnderRoot_ResolvesToLongForm_IsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short-name expansion is Windows-only");

        using var dir = new TempDir();
        string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
        // A directory whose name is long enough to get a distinct 8.3 alias on a short-name-enabled volume.
        string longNamed = Directory.CreateDirectory(
            Path.Combine(allowed, "A Very Long Directory Name 2026")).FullName;

        string? shortForm = GetShortPath(longNamed);

        // If 8.3 generation is disabled on the temp volume the alias equals the long form (or is null);
        // skip WITH a visible reason rather than asserting a non-existent behavior.
        Assert.SkipWhen(shortForm is null
            || string.Equals(shortForm, longNamed, StringComparison.OrdinalIgnoreCase),
            "no distinct 8.3 short alias on this volume (8dot3name likely disabled)");

        // The short-name destination is genuinely under the allowed root; the guard must expand it to
        // the canonical long form and ACCEPT it, proving the short name is not a blind spot.
        var r = CanonicalPathGuard.Check((shortForm + "/file.mkv").Replace('\\', '/'), [allowed]);

        Assert.True(r.Accepted, r.Reason);
        // The resolved target is the LONG form, not the PROGRA~1-style alias.
        Assert.DoesNotContain("~", r.ResolvedTarget);
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

    /// <summary>
    /// GATE-03 special-prefix rejection: extended-length (<c>\\?\</c>), DOS device (<c>\\.\</c>), and UNC
    /// (<c>\\server\share</c>) destinations are REJECTED unless an allowed root is byte-for-byte that
    /// exact prefix form. The <c>\\?\</c> case is the load-bearing one — that prefix tells Windows to
    /// SKIP <c>..</c> normalization, so a <c>\\?\C:\allowed\..\..\Windows</c> would otherwise escape with
    /// its <c>..</c> intact.
    /// </summary>
    /// <remarks>
    /// Nested, and separately tiered, on purpose. These fire on the syntax predicate BEFORE any disk
    /// resolution, so they are plain unit facts that touch no filesystem — L0, where the enclosing
    /// suite is L1. A tier trait is class-level, so folding them into the outer class would have
    /// relabelled four pure cases as needing a host double and dropped them out of an <c>L0</c>-only
    /// run. One file, two honestly-tiered classes.
    /// </remarks>
    [Trait("Tier", "L0")]
    public sealed class PrefixSyntax
    {
        [Fact]
        public void ExtendedLengthPrefix_WithParentTraversal_IsRejected()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "asserts Windows extended-length path semantics");

            // \\?\ disables `..` collapse; the guard must refuse it (rather than be fooled into letting
            // the un-collapsed `..` walk out of the allowlisted root).
            string target = @"\\?\C:\allowed\..\..\Windows".Replace('\\', '/');

            var r = CanonicalPathGuard.Check(target, [@"C:\allowed"]);

            Assert.False(r.Accepted);
            Assert.NotNull(r.Reason);
            Assert.Contains("device/UNC/extended-length", r.Reason);
        }

        [Fact]
        public void DosDevicePrefix_NotAllowlisted_IsRejected()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "asserts Windows DOS-device path semantics");

            string target = @"\\.\C:\allowed\sub".Replace('\\', '/');

            var r = CanonicalPathGuard.Check(target, [@"C:\allowed"]);

            Assert.False(r.Accepted);
            Assert.NotNull(r.Reason);
            Assert.Contains("device/UNC/extended-length", r.Reason);
        }

        [Fact]
        public void UncPath_NotAllowlisted_IsRejected()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "asserts Windows UNC path semantics");

            string target = @"\\server\share\media\out".Replace('\\', '/');

            var r = CanonicalPathGuard.Check(target, [@"C:\allowed"]);

            Assert.False(r.Accepted);
            Assert.NotNull(r.Reason);
            Assert.Contains("device/UNC/extended-length", r.Reason);
        }

        [Fact]
        public void UncTarget_WithUncAllowlistRoot_PassesThePrefixGate()
        {
            // An owner who deliberately allowlists a UNC root is honored: the prefix gate does NOT reject
            // a UNC target when a UNC root is present (it falls through to the disk-resolution step, which
            // then rejects only because the share does not resolve — proving the prefix gate itself let it
            // through rather than short-circuiting on syntax).
            string target = @"\\server\share\media\out".Replace('\\', '/');

            var r = CanonicalPathGuard.Check(target, [@"\\server\share"]);

            Assert.False(r.Accepted);
            Assert.NotNull(r.Reason);
            // It got PAST the syntax gate (no "device/UNC/extended-length" reason) and was rejected by the
            // canonical-resolution step instead (the unreachable share fails closed).
            Assert.DoesNotContain("device/UNC/extended-length", r.Reason);
        }
    }
}
