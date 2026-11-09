using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests.Engine;

// The derived $resolution and $height labels, and the de-duplication that keeps a title's own
// trailing resolution tag from being rendered beside one this template writes.
public class TemplateEngineResolutionTests
{
    private static IReadOnlyDictionary<string, string> NoTokens => new Dictionary<string, string>();
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NoMulti =>
        new Dictionary<string, IReadOnlyList<string>>();

    private static RenamerResult Render(
        string filename,
        IReadOnlyDictionary<string, string>? tokens = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? multi = null,
        RenamerOptions? options = null,
        string folder = "")
    {
        options ??= new RenamerOptions { FilenameTemplate = filename, FolderTemplate = folder };
        if (options.FilenameTemplate != filename || options.FolderTemplate != folder)
        {
            options = options with { FilenameTemplate = filename, FolderTemplate = folder };
        }
        return TemplateEngine.Render(tokens ?? NoTokens, multi ?? NoMulti, options);
    }



    private static Dictionary<string, string> Dimensions(string title, string? width, string? height)
    {
        var tokens = new Dictionary<string, string> { ["title"] = title };
        if (width is not null)
        {
            tokens["width"] = width;
        }

        if (height is not null)
        {
            tokens["height"] = height;
        }

        return tokens;
    }

    [Theory]
    [InlineData("3840", "2160", "X [4K]")]
    // A portrait file carries the label of its landscape twin, as Cove's own badge does.
    [InlineData("1080", "1920", "X [1080p]")]
    public void CoreTokens_Resolution_DerivedFromBothDimensions(string width, string height, string expected)
    {
        var r = Render("$title{ [$resolution]}", Dimensions("X", width, height));
        Assert.Equal(expected, r.Filename);
    }

    [Theory]
    [InlineData(null, "1920")]
    [InlineData("1920", null)]
    // Cove stores an unknown dimension as 0, which is the shape of a file Cove never probed.
    [InlineData("0", "2160")]
    [InlineData("1920", "0")]
    public void CoreTokens_Resolution_UnknownDimension_DropsTheResolutionGroup(string? width, string? height)
    {
        var r = Render("$title{ [$resolution]}", Dimensions("X", width, height));
        Assert.Equal("X", r.Filename);
    }

    [Fact]
    public void CoreTokens_Resolution_CallerSuppliedValue_WinsOverTheDerivedOne()
    {
        var tokens = Dimensions("Upright", "1080", "1920");
        tokens["resolution"] = "vertical";

        var r = Render("$title{ [$resolution]}", tokens);

        Assert.Equal("Upright [vertical]", r.Filename);
    }

    [Theory]
    [InlineData("Bootie From Beijing [1080p]", "1920", "1080", "Bootie From Beijing [1080p]")]
    [InlineData("Shot in [1080p] Glory", "3840", "2160", "Shot in [1080p] Glory [4K]")]
    [InlineData("Old Rip [720p]", "3840", "2160", "Old Rip [4K]")]
    // A tag no bucket is named for: the generic trailing-[<digits>p] strip removes it anyway.
    [InlineData("Nikki [368p]", "654", "368", "Nikki [360p]")]
    // The stripper matches case-insensitively, and the widest label is in its vocabulary.
    [InlineData("Old Rip [4k]", "3840", "2160", "Old Rip [4K]")]
    [InlineData("Old Rip [4K]", "3840", "2160", "Old Rip [4K]")]
    [InlineData("Old Rip [HUGE]", "3840", "2160", "Old Rip [4K]")]
    // A bracketed number with no 'p' is a serial, index or scene number, never a resolution.
    [InlineData("Calendar Audition [28]", "3840", "2160", "Calendar Audition [28] [4K]")]
    [InlineData("Blowjob [caufkb2cd9]", "1920", "1080", "Blowjob [caufkb2cd9] [1080p]")]
    [InlineData("Shot [720p] Take [5]", "1920", "1080", "Shot [720p] Take [5] [1080p]")]
    // A height with no stored width derives no label, so the title's own tag is the only one left.
    [InlineData("Movie [1080p]", null, "1080", "Movie [1080p]")]
    // 120 x 100 is under the smallest bucket on its long edge and under the smallest standard label's
    // margin on its short one, so Cove names that frame no resolution and the title keeps its tag.
    [InlineData("Clip [100p]", "120", "100", "Clip [100p]")]
    public void TrailingResolutionTag_IsReplaced_OnlyByALabelThisRenderDerives(
        string title, string? width, string? height, string expected)
    {
        var r = Render("$title{ [$resolution]}", Dimensions(title, width, height));
        Assert.Equal(expected, r.Filename);
    }

    [Fact]
    public void TrailingResolution_InTitle_Kept_WhenTemplateHasNoResolution()
    {
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "Bootie From Beijing [1080p]",
            ["width"] = "1920",
            ["height"] = "1080",
        };
        var r = Render("$title", tokens);
        Assert.Equal("Bootie From Beijing [1080p]", r.Filename);
    }

    [Theory]
    // A token name runs to the end of its letters, digits and underscores, so this template names an
    // unknown token rather than $resolution. It resolves empty, its group collapses, and the title
    // keeps the tag it came with.
    [InlineData("$title{ [$resolutionx]}", "Bootie From Beijing [1080p]")]
    // Token lookup is case-insensitive, so this template does name $resolution. The title's own tag
    // is stripped and the derived label is appended in its place, leaving one.
    [InlineData("$title{ [$RESOLUTION]}", "Bootie From Beijing [1080p]")]
    public void TrailingResolution_TokenNameBoundary_DecidesWhetherTagIsStripped(
        string template, string expected)
    {
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "Bootie From Beijing [1080p]",
            ["width"] = "1920",
            ["height"] = "1080",
        };
        var r = Render(template, tokens);
        Assert.Equal(expected, r.Filename);
    }

    [Theory]
    [InlineData("Movie [4K]", "1920", "1080", 12)]
    // This row's title tag is longer than the label the reducer drops, so keeping it would push the
    // name back over the budget and into a hard truncation.
    [InlineData("Movie [1080p]", "3840", "2160", 9)]
    public void TitleHasResolution_LengthReducerDropsResolution_DropsTheTitleTagToo(
        string title, string width, string height, int cap)
    {
        // The caps are the premise: the first render is over budget, so DropOrder is walked as far as
        // resolution and the render that survives writes no label of its own.
        var tokens = new Dictionary<string, string>
        {
            ["title"] = title,
            ["width"] = width,
            ["height"] = height,
        };
        var options = new RenamerOptions { FilenameMax = cap, FullPathMax = cap };
        var r = Render("$title{ [$resolution]}", tokens, options: options);
        Assert.Equal("Movie", r.Filename);
    }

    [Fact]
    public void TitleHasResolution_LengthReducerDropsResolution_FolderTemplate_DropsTheTitleTagToo()
    {
        // The filename template never renders $resolution, so nothing de-duplicated its title and the
        // drop reaches only the folder.
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "Movie [1080p]",
            ["width"] = "3840",
            ["height"] = "2160",
        };
        var options = new RenamerOptions { FilenameMax = 20, FullPathMax = 20 };
        var r = Render("$title", tokens, options: options, folder: "$title{ [$resolution]}");
        Assert.Equal("Movie", r.FolderPath);
        Assert.Equal("Movie [1080p]", r.Filename);
    }

    [Theory]
    [InlineData("$title{ [$resolution]}", "$title")]
    [InlineData("$title", "$title{ [$resolution]}")]
    public void TitleHasResolution_FolderTemplate_CarriesTheLabelExactlyOnce(
        string filenameTemplate, string folderTemplate)
    {
        // Each template decides the de-duplication against its own text, so neither render's decision
        // reaches the other.
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "Movie [1080p]",
            ["width"] = "1920",
            ["height"] = "1080",
        };
        var r = Render(filenameTemplate, tokens, folder: folderTemplate);
        Assert.Equal("Movie [1080p]", r.FolderPath);
    }

    [Fact]
    public void Resolution_DerivedFromTheStoredDimensions_NotTheRewrittenOnes()
    {
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "Movie",
            ["width"] = "1920",
            ["height"] = "1080",
        };
        var options = new RenamerOptions
        {
            FieldReplacers =
            [
                new FieldReplaceRule { TargetToken = "width", Find = "1920", Replace = "1920px" },
            ],
        };
        Assert.Equal("1920px", TemplateEngine.ResolveField(tokens, NoMulti, options, "width"));
        Assert.Equal("1080p", TemplateEngine.ResolveField(tokens, NoMulti, options, "resolution"));
    }

    [Fact]
    public void Resolution_HeightReplacementRule_DoesNotChangeTheLabel()
    {
        // A label read off the rewritten height would be 1440p, which is the label Cove shows for a
        // 1920 x 2160 frame and not the one it shows for this file.
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "Movie",
            ["width"] = "1920",
            ["height"] = "1080",
        };
        var options = new RenamerOptions
        {
            FieldReplacers =
            [
                new FieldReplaceRule { TargetToken = "height", Find = "1080", Replace = "2160" },
            ],
        };
        var r = Render("$title{ [$resolution]}", tokens, options: options);
        Assert.Equal("Movie [1080p]", r.Filename);
    }

    [Fact]
    public void Resolution_ReplacementRule_RewritesTheDerivedLabel()
    {
        // The strip asks whether this render writes a label, not whether the label is one the fixed
        // vocabulary names, so a rewritten label still displaces the title's own tag.
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "Movie [1080p]",
            ["width"] = "1920",
            ["height"] = "1080",
        };
        var options = new RenamerOptions
        {
            FieldReplacers =
            [
                new FieldReplaceRule { TargetToken = "resolution", Find = "1080p", Replace = "FHD" },
            ],
        };
        var r = Render("$title{ [$resolution]}", tokens, options: options);
        Assert.Equal("Movie [FHD]", r.Filename);
    }

    [Fact]
    public void Height_RendersTheStoredHeight_WhileResolutionRoundsToCovesBucket()
    {
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "Alyssa",
            ["width"] = "768",
            ["height"] = "432",
        };
        var r = Render("$title [$height] [$resolution]", tokens);
        Assert.Equal("Alyssa [432p] [480p]", r.Filename);
    }

    [Fact]
    public void Height_UnknownHeight_DropsItsGroup()
    {
        var r = Render("X{ [$height]}", new Dictionary<string, string> { ["height"] = "0" });
        Assert.Equal("X", r.Filename);
    }

    [Theory]
    [InlineData("Alyssa [432p]")]
    [InlineData("Alyssa [480p]")]
    public void TrailingResolution_InTitle_NotDoubled_WhenTemplateAppendsHeight(string title)
    {
        var tokens = new Dictionary<string, string>
        {
            ["title"] = title,
            ["height"] = "432",
        };
        var r = Render("$title{ [$height]}", tokens);
        Assert.Equal("Alyssa [432p]", r.Filename);
    }

    [Fact]
    public void TitleHasResolution_LengthReducerDropsHeight_DropsTheTitleTagToo()
    {
        // "Movie [4K]" is over the cap, and the title tag is longer than the dropped label, so keeping
        // it would push the name back over the budget and into a hard truncation.
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "Movie [1080p]",
            ["height"] = "2160",
        };
        var options = new RenamerOptions { FilenameMax = 9, FullPathMax = 9, DropOrder = ["height"] };
        var r = Render("$title{ [$height]}", tokens, options: options);
        Assert.Equal("Movie", r.Filename);
    }
}
