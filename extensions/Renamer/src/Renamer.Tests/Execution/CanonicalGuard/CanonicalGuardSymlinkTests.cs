using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.CanonicalGuard;

/// <summary>
/// GATE-03 symlink variant: a directory SYMBOLIC LINK inside an allowed root pointing OUTSIDE it is
/// REJECTED — the same <c>ResolveLinkTarget</c> chain that the mandatory junction test proves also
/// resolves symlinks. Unlike junctions, creating a directory symlink needs Developer Mode or admin
/// privilege, so this one can skip. The junction test remains the non-skippable load-bearing proof of
/// the resolution path.
/// </summary>
[Trait("Tier", "L1")]
[Trait("Adversarial", "Symlink")]
public sealed class CanonicalGuardSymlinkTests
{
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

    [Fact]
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
}
