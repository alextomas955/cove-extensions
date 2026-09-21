using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests.Engine;

/// <summary>
/// Pins every label <see cref="ResolutionLabel"/> emits against the label Cove's own resolution
/// badge shows for the same dimensions, and pins the width-absent fallback's two known
/// disagreements with the two-dimension answer.
/// </summary>
public class ResolutionLabelTests
{
    [Theory]
    [InlineData(7680, 4320, "8K")]
    [InlineData(5120, 2880, "5K")]
    [InlineData(6144, 3384, "6K")]
    [InlineData(1080, 1920, "1080p")]
    [InlineData(720, 1280, "720p")]
    [InlineData(1024, 576, "540p")]
    [InlineData(960, 540, "540p")]
    [InlineData(3840, 2160, "4K")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1280, 720, "720p")]
    [InlineData(2560, 1440, "1440p")]
    [InlineData(4096, 2160, "4K")]
    [InlineData(1920, 1200, "1080p")]
    [InlineData(1440, 1080, "1080p")]
    [InlineData(854, 480, "480p")]
    [InlineData(640, 480, "480p")]
    [InlineData(640, 360, "360p")]
    [InlineData(426, 240, "240p")]
    [InlineData(256, 144, "144p")]
    [InlineData(6000, 4000, "7K")]
    [InlineData(12000, 6000, "HUGE")]
    public void FromDimensions_MatchesCoveBadge(int width, int height, string expected)
    {
        Assert.Equal(expected, ResolutionLabel.FromDimensions(width, height));
    }

    // The portrait heights are the fallback's known limit: one dimension cannot tell a tall video
    // from a wide one, so 1920 and 1280 answer as the landscape file of that height.
    [Theory]
    [InlineData(4320, "8K")]
    [InlineData(3384, "6K")]
    [InlineData(2880, "5K")]
    [InlineData(2160, "4K")]
    [InlineData(1920, "1440p")]
    [InlineData(1440, "1440p")]
    [InlineData(1280, "1080p")]
    [InlineData(1200, "1080p")]
    [InlineData(1080, "1080p")]
    [InlineData(720, "720p")]
    [InlineData(576, "540p")]
    [InlineData(540, "540p")]
    [InlineData(480, "480p")]
    [InlineData(432, "360p")]
    [InlineData(368, "360p")]
    [InlineData(360, "360p")]
    [InlineData(240, "240p")]
    [InlineData(144, "144p")]
    [InlineData(143, "144p")]
    [InlineData(137, "144p")]
    public void FromHeight_MatchesCoveBadgeForTheLandscapeFileOfThatHeight(int height, string expected)
    {
        Assert.Equal(expected, ResolutionLabel.FromHeight(height));
    }

    [Theory]
    [InlineData(136)]
    [InlineData(100)]
    [InlineData(0)]
    [InlineData(-1)]
    public void FromHeight_TooSmallOrNonPositive_ReturnsEmpty(int height)
    {
        Assert.Equal(string.Empty, ResolutionLabel.FromHeight(height));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, -1)]
    public void FromDimensions_NonPositiveDimension_ReturnsEmpty(int width, int height)
    {
        Assert.Equal(string.Empty, ResolutionLabel.FromDimensions(width, height));
    }

    [Fact]
    public void KnownLabels_CarriesEveryEmittableLabelAndNothingElse()
    {
        string[] expected =
        [
            "144p", "240p", "360p", "480p", "540p", "720p", "1080p", "1440p",
            "4K", "5K", "6K", "7K", "8K", "HUGE",
        ];

        Assert.Equal(
            expected.OrderBy(l => l, StringComparer.Ordinal),
            ResolutionLabel.KnownLabels.OrderBy(l => l, StringComparer.Ordinal));
    }

    private static RenamerResult Render(IReadOnlyDictionary<string, string> tokens)
        => TemplateEngine.Render(
            tokens,
            new Dictionary<string, IReadOnlyList<string>>(),
            new RenamerOptions { FilenameTemplate = "$title{ [$resolution]}", FolderTemplate = "" });

    [Fact]
    public void Render_BothDimensionTokens_LabelsThePortraitVideoByItsShortEdge()
    {
        var r = Render(new Dictionary<string, string>
        {
            ["title"] = "Upright",
            ["width"] = "1080",
            ["height"] = "1920",
        });

        Assert.Equal("Upright [1080p]", r.Filename);
    }

    [Fact]
    public void Render_HeightTokenOnly_FallsBackToTheHeightAsShortEdge()
    {
        var r = Render(new Dictionary<string, string>
        {
            ["title"] = "Upright",
            ["height"] = "1920",
        });

        Assert.Equal("Upright [1440p]", r.Filename);
    }

    // Cove stores an unknown width as 0, so the projector emits a "0" width token for any file whose
    // width was never probed. Such a file still gets the label its height gives.
    [Fact]
    public void Render_ZeroWidthToken_FallsBackToTheHeight()
    {
        var r = Render(new Dictionary<string, string>
        {
            ["title"] = "Unprobed",
            ["width"] = "0",
            ["height"] = "2160",
        });

        Assert.Equal("Unprobed [4K]", r.Filename);
    }

    [Fact]
    public void Render_CallerSuppliedResolution_WinsOverTheDerivedOne()
    {
        var r = Render(new Dictionary<string, string>
        {
            ["title"] = "Upright",
            ["width"] = "1080",
            ["height"] = "1920",
            ["resolution"] = "vertical",
        });

        Assert.Equal("Upright [vertical]", r.Filename);
    }
}
