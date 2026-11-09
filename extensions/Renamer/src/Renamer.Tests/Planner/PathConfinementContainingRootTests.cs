using Renamer.Planner;

namespace Renamer.Tests.Planner;

public sealed class PathConfinementContainingRootTests
{
    [Fact]
    public void PathUnderARoot_ReturnsThatRoot()
        => Assert.Equal("/media", PathConfinement.ContainingRoot("/media/a/b.mkv", ["/media"]));

    [Theory]
    [InlineData("/media", "/media/video")]
    [InlineData("/media/video", "/media")]
    public void NestedRoots_LongestWins_InEitherDeclarationOrder(string first, string second)
        => Assert.Equal(
            "/media/video",
            PathConfinement.ContainingRoot("/media/video/a.mkv", [first, second]));

    [Theory]
    [MemberData(nameof(PathsWithNoContainingRoot))]
    public void NoContainingRoot_ReturnsNull(string path, string[] roots)
        => Assert.Null(PathConfinement.ContainingRoot(path, roots));

    public static TheoryData<string, string[]> PathsWithNoContainingRoot() => new()
    {
        { "/elsewhere/a.mkv", ["/media"] },     // the path is under no root
        { "/media/a.mkv", [] },                 // no roots at all
        { "/elsewhere/a.mkv", ["", "   "] },    // a blank entry is ignored, not a root matching everything
        { "/mediaEvil/a.mkv", ["/media"] },     // a sibling sharing the root's textual prefix
    };

    [Fact]
    public void TheAnswerIsTheNormalizedEntry_NotTheOneSupplied()
    {
        // The one-time options conversion writes this return straight into the stored destination
        // root, which the panel re-checks against the library-path list it was offered.
        Assert.Equal(
            "D:/media",
            PathConfinement.ContainingRoot("D:/media/a.mkv", [@"D:\media\"]));
    }
}
