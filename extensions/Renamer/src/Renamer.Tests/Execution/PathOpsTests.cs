using Renamer.Execution;

namespace Renamer.Tests.Execution;

/// <summary>
/// Pins the shared path string math, and specifically the boundary cases where the planner, the
/// executor and the undo replayer each used to carry their own copy: an empty part on either side of
/// a join, a separator that arrived in the other form, and a dot that is not an extension boundary.
/// PURE — no disk.
/// </summary>
public sealed class PathOpsTests
{
    [Theory]
    [InlineData("media/videos", "film.mkv", "media/videos/film.mkv")]
    [InlineData("media/videos/", "film.mkv", "media/videos/film.mkv")]
    [InlineData("media/videos", "/film.mkv", "media/videos/film.mkv")]
    [InlineData("", "film.mkv", "film.mkv")]
    [InlineData("media/videos", "", "media/videos")]
    [InlineData(@"media\videos", "film.mkv", "media/videos/film.mkv")]
    public void JoinPath_IsForwardSlash_AndToleratesAnEmptyOrSeparatedPart(string a, string b, string expected)
        => Assert.Equal(expected, PathOps.JoinPath(a, b));

    [Theory]
    [InlineData("film.mkv", "film", ".mkv")]
    [InlineData("film.en.vtt", "film.en", ".vtt")]
    [InlineData("README", "README", "")]
    [InlineData(".gitignore", ".gitignore", "")]
    public void SplitBasename_SplitsAtTheFinalDot_AndNeverAtALeadingOne(string basename, string filename, string ext)
    {
        var (actualName, actualExt) = PathOps.SplitBasename(basename);

        Assert.Equal(filename, actualName);
        Assert.Equal(ext, actualExt);
    }

    [Theory]
    [InlineData("film.mkv", "film")]
    [InlineData("film.en.vtt", "film.en")]
    [InlineData("README", "README")]
    [InlineData(".gitignore", ".gitignore")]
    public void StemOf_DropsOnlyTheFinalExtension(string basename, string expected)
        => Assert.Equal(expected, PathOps.StemOf(basename));

    [Fact]
    public void ApplySuffix_PutsTheCounterBeforeTheExtension()
        => Assert.Equal("film (2).mkv", PathOps.ApplySuffix("film", ".mkv", " ({n})", 2));

    [Fact]
    public void ApplySuffix_OnAnExtensionlessName_AppendsTheSuffix()
        => Assert.Equal("README (1)", PathOps.ApplySuffix("README", "", " ({n})", 1));
}
