using Renamer.Engine;

namespace Renamer.Tests.Engine;

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
    [InlineData(143, 137, "144p")]
    public void FromDimensions_ReturnsExpectedLabel(int width, int height, string expected)
    {
        Assert.Equal(expected, ResolutionLabel.FromDimensions(width, height));
    }

    [Theory]
    [InlineData(432, "432p")]
    [InlineData(1080, "1080p")]
    [InlineData(1440, "1440p")]
    [InlineData(2159, "2159p")]
    [InlineData(2160, "4K")]
    [InlineData(2880, "5K")]
    [InlineData(3383, "5K")]
    [InlineData(3384, "6K")]
    [InlineData(4032, "7K")]
    [InlineData(4320, "8K")]
    [InlineData(9000, "8K")]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    public void FromHeight_ReturnsExpectedLabel(int height, string expected)
    {
        Assert.Equal(expected, ResolutionLabel.FromHeight(height));
    }

    [Theory]
    [InlineData(143, 136)]
    [InlineData(136, 136)]
    [InlineData(100, 100)]
    public void FromDimensions_TooSmallForAnyLabel_ReturnsEmpty(int width, int height)
    {
        Assert.Equal(string.Empty, ResolutionLabel.FromDimensions(width, height));
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
}
