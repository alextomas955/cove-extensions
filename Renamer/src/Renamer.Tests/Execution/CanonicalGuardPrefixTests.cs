using Renamer.Execution;

namespace Renamer.Tests.Execution;

/// <summary>
/// GATE-03 special-prefix rejection: extended-length (<c>\\?\</c>), DOS device (<c>\\.\</c>), and UNC
/// (<c>\\server\share</c>) destinations are REJECTED unless an allowed root is byte-for-byte that exact
/// prefix form. The <c>\\?\</c> case is the load-bearing one — that prefix tells Windows to SKIP
/// <c>..</c> normalization, so a <c>\\?\C:\allowed\..\..\Windows</c> would otherwise escape with its
/// <c>..</c> intact. These fire on the syntax predicate BEFORE any disk resolution, so they are plain
/// unit facts (no filesystem touch).
/// </summary>
public sealed class CanonicalGuardPrefixTests
{
    [Fact]
    public void ExtendedLengthPrefix_WithParentTraversal_IsRejected()
    {
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
        string target = @"\\.\C:\allowed\sub".Replace('\\', '/');

        var r = CanonicalPathGuard.Check(target, [@"C:\allowed"]);

        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
        Assert.Contains("device/UNC/extended-length", r.Reason);
    }

    [Fact]
    public void UncPath_NotAllowlisted_IsRejected()
    {
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
