using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

public sealed class PathConfinementTests
{
    private const string Root = "media/videos";

    private static PathConfinement.ConfinementResult Resolve(
        string folder, string basename = "film.mkv", RenamerOptions? options = null)
        => PathConfinement.Resolve(Root, folder, basename, options ?? new RenamerOptions());

    [Theory]
    [InlineData("", "/media/videos")]                           // empty folder: in place, at the root
    [InlineData("Acme/2024", "/media/videos/Acme/2024")]
    public void ARelativeFolder_IsAccepted_UnderTheRoot(string destinationFolder, string expectedSuffix)
    {
        var r = Resolve(destinationFolder);

        Assert.True(r.Accepted);
        Assert.EndsWith(expectedSuffix, r.TargetFolderPath);
    }

    [Fact]
    public void ParentTraversal_Escape_Rejected()
    {
        var r = Resolve("../../escape");

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
        Assert.Contains("escapes", r.Reason);
    }

    [Theory]
    [MemberData(nameof(RootedFolderTemplates))]
    public void RootedFolderTemplate_Rejected(string destinationFolder)
    {
        var r = Resolve(destinationFolder);

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
        Assert.Contains("not relative", r.Reason);
    }

    public static TheoryData<string> RootedFolderTemplates()
    {
        var cells = new TheoryData<string> { "/etc" };

        if (OperatingSystem.IsWindows())
        {
            cells.Add(@"C:\Windows");
            cells.Add(@"\\server\share");
        }

        return cells;
    }

    [Fact]
    public void Sibling_NotMistakenForChild_Rejected()
    {
        // Resolving "../videosEvil" from "media/videos" lands on a sibling "media/videosEvil"
        // whose absolute form shares the textual prefix of the root but is not under it.
        var r = Resolve("../videosEvil");

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
    }

    [Fact]
    public void OverFullPathMax_AbsoluteTarget_RejectedAsTooLong()
    {
        var r = Resolve("", new string('x', 300) + ".mkv", new RenamerOptions { FullPathMax = 40 });

        Assert.Equal(PathConfinement.ConfinementRejection.TooLong, r.Rejection);
        Assert.Contains("FullPathMax", r.Reason);
    }

    [Fact]
    public void EscapeAndOverLength_ReportsTheEscape_NotTheLength()
    {
        // A destination refused outright must never be reported as merely too long: the two ask a
        // user for opposite actions.
        var r = Resolve("../../escape", new string('x', 300) + ".mkv", new RenamerOptions { FullPathMax = 40 });

        Assert.Equal(PathConfinement.ConfinementRejection.NotAllowed, r.Rejection);
    }
}
