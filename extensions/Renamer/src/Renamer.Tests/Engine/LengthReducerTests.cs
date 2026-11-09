using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests.Engine;

public class LengthReducerTests
{
    // A filename template referencing many tokens, including the multi-value $performers/$tags
    // fields and every default drop-order field.
    private const string FilenameTemplate =
        "$studio - $studioCode - $title - $performers " +
        "[$resolution $videoCodec $audioCodec $frameRate] {$tags} ($date)";

    // A deliberately long title, so that even after every drop-order field is removed the title
    // alone still exceeds the 255-char filename cap and forces the hard-truncate last resort.
    private const string LongTitle =
        "The Exceedingly Verbose And Deliberately Overlong Documentary Title That Keeps " +
        "Going Well Past Any Reasonable Filesystem Component Length Limit In Order To " +
        "Exercise Every Single Field Drop And Then The Final Hard Truncate Of The Title " +
        "Itself So That Even With Every Other Drop-Order Field Removed The Bare Title " +
        "Alone Still Exceeds Two Hundred And Fifty Five Characters And Must Be Cut Short";

    private static readonly Dictionary<string, string> Tokens = new()
    {
        ["title"] = LongTitle,
        ["studio"] = "Some Very Long Studio Production Company Name International",
        ["studioCode"] = "STUDIOCODE-0000000001",
        ["resolution"] = "2160p",
        ["videoCodec"] = "h265-hevc-main10",
        ["audioCodec"] = "truehd-atmos-7point1",
        ["frameRate"] = "59.94fps",
        ["date"] = "2026-06-27",
        ["ext"] = "mkv",
    };

    private static readonly IReadOnlyList<string> Performers =
    [
        "Alexandria Featherstonehaugh",
        "Bartholomew Fitzgerald-Montgomery",
        "Cassandra Wollstonecraft-Bennett",
        "Demetrius Aurelius Constantinopoulos",
        "Evangeline Marchetti-Hawthorne",
    ];

    private static readonly IReadOnlyList<string> Tags =
    [
        "documentary-feature-length",
        "remastered-restoration-4k",
        "criterion-collection-edition",
        "behind-the-scenes-commentary",
        "extended-directors-cut",
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FixtureMulti =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = Performers,
            ["tags"] = Tags,
        };

    [Fact]
    public void FitsBoth_FilenameOverCap_FailsEvenWhenPathWouldFit()
    {
        var o = new RenamerOptions { FilenameMax = 255, FullPathMax = 1000 };
        string name = new string('a', 300);
        Assert.False(LengthReducer.FitsBoth("", name, ".mkv", o));
    }

    [Fact]
    public void FitsBoth_PathOverCap_FailsEvenWhenFilenameFits()
    {
        var o = new RenamerOptions { FilenameMax = 255, FullPathMax = 259 };
        string folder = new string('d', 300);
        string name = "short";
        Assert.False(LengthReducer.FitsBoth(folder, name, ".mkv", o));
    }

    [Fact]
    public void FitsBoth_BothUnderCaps_True()
    {
        var o = new RenamerOptions();
        Assert.True(LengthReducer.FitsBoth("folder", "name", ".mkv", o));
    }

    [Fact]
    public void RenderWithDropped_ShortName_DropsNothing()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "Movie", ["ext"] = "mkv" };
        var options = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "" };

        var (result, dropped) = TemplateEngine.RenderWithDropped(
            tokens, new Dictionary<string, IReadOnlyList<string>>(), options);

        Assert.Equal("Movie", result.Filename);
        Assert.Equal(".mkv", result.Ext);
        Assert.Empty(dropped);
    }

    [Fact]
    public void RenderWithDropped_LongFixture_DropsEveryField_ThenTruncatesTheTitleToTheBudget()
    {
        var options = new RenamerOptions
        {
            FilenameTemplate = FilenameTemplate,
            FolderTemplate = "",
            FilenameMax = 255,
        };

        var (r, dropped) = TemplateEngine.RenderWithDropped(Tokens, FixtureMulti, options);

        // The fixture exhausts every drop-order field, so the reducer reports the full DropOrder in order.
        Assert.Equal(options.DropOrder, dropped);
        foreach (var field in new[] { "studio", "studioCode", "resolution", "videoCodec", "audioCodec", "frameRate", "date" })
        {
            Assert.DoesNotContain(Tokens[field], r.Filename);
        }

        Assert.All(Performers, p => Assert.DoesNotContain(p, r.Filename));
        Assert.All(Tags, t => Assert.DoesNotContain(t, r.Filename));

        // The bare title still overruns, so the hard truncate cuts the name to exactly the filename
        // budget: FilenameMax less the extension, which is tighter than the full-path budget here.
        Assert.Equal(".mkv", r.Ext);
        Assert.Equal(251, r.Filename.Length);
        Assert.Contains(LongTitle[..100], r.Filename);
        Assert.True(LengthReducer.FitsBoth(r.FolderPath, r.Filename, r.Ext, options));
    }

    [Fact]
    public void Fit_ShortNameDeepFolder_DropsTheFolderFieldAndKeepsTheName()
    {
        // The folder alone overruns the full-path cap, and studio is a drop-order field.
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "Short",
            ["studio"] = new string('S', 300),
            ["ext"] = "mkv",
        };
        var options = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "$studio",
        };
        var r = TemplateEngine.Render(tokens, new Dictionary<string, IReadOnlyList<string>>(), options);

        Assert.Equal("", r.FolderPath);
        Assert.Equal("Short", r.Filename);
    }

    // "a" ×9, then U+1F600 (a surrogate pair, code units 9 and 10), then filler to force a truncate.
    private static readonly string PairAtNine = new string('a', 9) + "\U0001F600" + new string('b', 40);

    [Fact]
    public void Fit_BudgetLandsInsideASurrogatePair_TruncatesOneUnitShorter()
    {
        var o = new RenamerOptions { FilenameMax = 14, FullPathMax = 1000 }; // budget 14 - 4 = 10

        var r = LengthReducer.Fit("", PairAtNine, ".mp4", o, _ => ("", PairAtNine)).result;

        Assert.Equal(9, r.Filename.Length);
        Assert.DoesNotContain(r.Filename, char.IsSurrogate);
    }

    [Fact]
    public void Fit_BudgetLandsAfterASurrogatePair_KeepsTheWholePair()
    {
        // The control: a budget ending on the low half is already a character boundary and must not
        // lose a unit, so the shortening is keyed on the pair and not applied to every truncate.
        var o = new RenamerOptions { FilenameMax = 15, FullPathMax = 1000 }; // budget 15 - 4 = 11

        var r = LengthReducer.Fit("", PairAtNine, ".mp4", o, _ => ("", PairAtNine)).result;

        Assert.Equal(11, r.Filename.Length);
        Assert.EndsWith("\U0001F600", r.Filename, StringComparison.Ordinal);
    }
}
