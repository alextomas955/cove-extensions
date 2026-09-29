using Renamer.Planner;

namespace Renamer.Tests.Planner;

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

    [Theory]
    [InlineData("film", ".mkv", " ({n})", 2, "film (2).mkv")]
    [InlineData("README", "", " ({n})", 1, "README (1)")]
    // A format naming the counter more than once, and one naming it not at all.
    [InlineData("film", ".mkv", "-{n}-{n}", 3, "film-3-3.mkv")]
    [InlineData("film", ".mkv", "no-token", 5, "filmno-token.mkv")]
    public void ApplySuffix_PutsEveryTokenBeforeTheExtension_AndAppendsAFormatHoldingNone(
        string filename, string ext, string format, int counter, string expected)
        => Assert.Equal(expected, PathOps.ApplySuffix(filename, ext, format, counter));
}
