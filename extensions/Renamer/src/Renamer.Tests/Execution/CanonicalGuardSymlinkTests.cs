using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// GATE-03 symlink variant: a directory SYMBOLIC LINK inside an allowed root pointing OUTSIDE it is
/// REJECTED — the same <c>ResolveLinkTarget</c> chain that the mandatory junction test proves also
/// resolves symlinks. Unlike junctions, creating a directory symlink needs Developer Mode or admin
/// privilege, so this is a <see cref="SkippableFactAttribute"/>: it probes by attempting to create one
/// and SKIPS WITH A VISIBLE REASON when privilege is absent — never a silent early-return. On a
/// privileged box it runs and asserts the escape is rejected. The junction test remains the
/// non-skippable load-bearing proof of the resolution path.
/// </summary>
[Trait("Tier", "Integration")]
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

    [SkippableFact]
    public void SymlinkInsideAllowedRoot_PointingOutside_IsRejected()
    {
        using var dir = new TempDir();
        string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")).FullName;
        string escape = Path.Combine(allowed, "escape");

        bool created = TryCreateSymlink(escape, outside);
        Skip.IfNot(created, "symlink creation requires Developer Mode/admin privilege on this host");

        var r = CanonicalPathGuard.Check((escape + "/file.mkv").Replace('\\', '/'), [allowed]);

        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
        Assert.Contains("outside every allowed root", r.Reason);
    }
}
