using WhisparrSync.Options;
using WhisparrSync.Safety;

namespace WhisparrSync.Tests.Safety;

/// <summary>
/// The Cove→Whisparr path-view rewrite <see cref="PathTranslationService.ToWhisparrView"/>: which rule fires,
/// which is skipped, and what an unmapped path comes back as.
/// </summary>
/// <remarks>
/// Two cases are load-bearing. A prefix matching only as raw text (<c>/data/media</c> against
/// <c>/data/media-extra</c>) must not fire, or a file on a sibling disk is rewritten onto a mount it does not
/// live under; and the comparison must stay case-sensitive, since the Linux/Docker target treats
/// <c>/data/Media</c> and <c>/data/media</c> as different directories.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class PathTranslationServiceTests
{
    [Fact]
    public void EmptyTable_ReturnsTheNormalizedInput()
    {
        Assert.Equal("/data/media/a.mkv", PathTranslationService.ToWhisparrView("/data/media/a.mkv", []));
    }

    [Fact]
    public void MatchingRule_RewritesOnlyThePrefix()
    {
        PathTranslationRule[] table = [new("/data/media", "/mnt/w")];

        Assert.Equal(
            "/mnt/w/movies/2024/A Scene (1080p).mkv",
            PathTranslationService.ToWhisparrView("/data/media/movies/2024/A Scene (1080p).mkv", table));
    }

    [Fact]
    public void FirstMatchingRuleWins()
    {
        PathTranslationRule[] table =
        [
            new("/data/media", "/mnt/first"),
            new("/data/media/movies", "/mnt/second"),
            new("/data", "/mnt/third"),
        ];

        Assert.Equal("/mnt/first/movies/a.mkv", PathTranslationService.ToWhisparrView("/data/media/movies/a.mkv", table));
    }

    [Fact]
    public void SiblingPrefix_DoesNotMatch()
    {
        PathTranslationRule[] table = [new("/data/media", "/mnt/w")];

        Assert.Equal("/data/media-extra/x.mp4", PathTranslationService.ToWhisparrView("/data/media-extra/x.mp4", table));
    }

    [Fact]
    public void ExactEquality_Matches()
    {
        PathTranslationRule[] table = [new("/data/media", "/mnt/w")];

        Assert.Equal("/mnt/w", PathTranslationService.ToWhisparrView("/data/media", table));
    }

    [Fact]
    public void SeparatorsAndTrailingSlashes_AreNormalizedOnBothSides()
    {
        PathTranslationRule[] table = [new(@"\data\media\", @"\mnt\w\")];

        Assert.Equal("/mnt/w/movies/a.mkv", PathTranslationService.ToWhisparrView(@"\data\media\movies\a.mkv\", table));
    }

    [Fact]
    public void ComparisonIsCaseSensitive()
    {
        PathTranslationRule[] table = [new("/data/Media", "/mnt/w")];

        Assert.Equal("/data/media/x.mp4", PathTranslationService.ToWhisparrView("/data/media/x.mp4", table));
    }

    [Fact]
    public void BlankCovePrefix_IsSkipped()
    {
        PathTranslationRule[] table = [new("", "/mnt/wrong"), new("/data/media", "/mnt/w")];

        Assert.Equal("/mnt/w/a.mkv", PathTranslationService.ToWhisparrView("/data/media/a.mkv", table));
    }
}
