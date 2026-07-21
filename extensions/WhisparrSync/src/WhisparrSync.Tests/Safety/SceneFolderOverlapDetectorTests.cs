using WhisparrSync.Safety;

namespace WhisparrSync.Tests.Safety;

// Host-free (references no Cove type), so this file stays OUT of the csproj bare-CI Compile-Remove group.
public sealed class SceneFolderOverlapDetectorTests
{
    [Theory]
    [InlineData("scenes/{Studio CleanNetwork}/{Studio CleanTitle}", "scenes")]
    [InlineData("scenes", "scenes")]
    [InlineData("/scenes/{Studio}", "scenes")]
    [InlineData("media/scenes/{Studio}", "media")]
    public void LeadingLiteralSegment_ReturnsFirstLiteralSegment(string format, string expected)
        => Assert.Equal(expected, SceneFolderOverlapDetector.LeadingLiteralSegment(format));

    [Theory]
    [InlineData("{Studio}/{Title}")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void LeadingLiteralSegment_NoLeadingLiteral_ReturnsNull(string? format)
        => Assert.Null(SceneFolderOverlapDetector.LeadingLiteralSegment(format));

    [Fact]
    public void Detect_RootTrailingSegmentEqualsLiteral_FlagsWithSuggestedParent()
    {
        var overlap = Assert.Single(
            SceneFolderOverlapDetector.Detect("scenes/{Studio}", ["/data/media/scenes"]));

        Assert.Equal("/data/media/scenes", overlap.Root);
        Assert.Equal("scenes", overlap.Prefix);
        Assert.Equal("/data/media", overlap.SuggestedRoot);
    }

    [Fact]
    public void Detect_RootWithTrailingSlash_StillFlags_Normalized()
    {
        var overlap = Assert.Single(
            SceneFolderOverlapDetector.Detect("scenes/{Studio}", ["/data/media/scenes/"]));

        Assert.Equal("/data/media/scenes/", overlap.Root); // reported as supplied
        Assert.Equal("/data/media", overlap.SuggestedRoot);
    }

    [Fact]
    public void Detect_CorrectlyConfiguredRoot_NoOverlap()
        => Assert.Empty(SceneFolderOverlapDetector.Detect("scenes/{Studio}", ["/data/media"]));

    [Fact]
    public void Detect_CaseMismatchedTrailingSegment_NoOverlap()
        => Assert.Empty(SceneFolderOverlapDetector.Detect("scenes/{Studio}", ["/data/media/Scenes"]));

    [Fact]
    public void Detect_TokenLeadingFormat_EmptyRegardlessOfRoots()
        => Assert.Empty(SceneFolderOverlapDetector.Detect("{Studio}/{Title}", ["/data/media/scenes"]));

    [Fact]
    public void Detect_EmptyFormat_Empty()
        => Assert.Empty(SceneFolderOverlapDetector.Detect("", ["/data/media/scenes"]));

    [Fact]
    public void Detect_NullRoots_Empty()
        => Assert.Empty(SceneFolderOverlapDetector.Detect("scenes/{Studio}", null));

    [Fact]
    public void Detect_EmptyRoots_Empty()
        => Assert.Empty(SceneFolderOverlapDetector.Detect("scenes/{Studio}", []));

    [Fact]
    public void Detect_EmptyOrWhitespaceRoots_AreIgnored()
        => Assert.Empty(SceneFolderOverlapDetector.Detect("scenes/{Studio}", ["", "   "]));
}
