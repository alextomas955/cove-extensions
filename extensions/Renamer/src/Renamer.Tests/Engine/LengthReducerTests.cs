using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests.Engine;

public class LengthReducerTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FixtureMulti =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = LongTemplateFixture.Performers,
            ["tags"] = LongTemplateFixture.Tags,
        };

    // ---- FitsBoth: measures both constraints (filename component and full path) ----

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
        string name = "short"; // name+ext fits 255, but folder+/+name+ext blows 259
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
        var options = new RenamerOptions { FilenameTemplate = LongTemplateFixture.FilenameTemplate, FolderTemplate = "" };

        var (r, dropped) = TemplateEngine.RenderWithDropped(LongTemplateFixture.Tokens, FixtureMulti, options);

        // The fixture exhausts every drop-order field, so the reducer reports the full DropOrder in order.
        Assert.Equal(options.DropOrder, dropped);
        foreach (var field in new[] { "studio", "studioCode", "resolution", "videoCodec", "audioCodec", "frameRate", "date" })
        {
            Assert.DoesNotContain(LongTemplateFixture.Tokens[field], r.Filename);
        }

        Assert.All(LongTemplateFixture.Performers, p => Assert.DoesNotContain(p, r.Filename));
        Assert.All(LongTemplateFixture.Tags, t => Assert.DoesNotContain(t, r.Filename));

        // The bare title still overruns, so the hard truncate cuts the name to exactly the filename
        // budget: FilenameMax less the extension, which is tighter than the full-path budget here.
        Assert.Equal(".mkv", r.Ext);
        Assert.Equal(options.FilenameMax - r.Ext.Length, r.Filename.Length);
        Assert.Contains(LongTemplateFixture.LongTitle[..100], r.Filename);
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

    // ---- The hard truncate cuts between characters, never through one ----

    // "a" ×9, then U+1F600 (a surrogate pair, code units 9 and 10), then filler to force a truncate.
    private static readonly string PairAtNine = new string('a', 9) + "\U0001F600" + new string('b', 40);

    [Fact]
    public void Fit_BudgetLandsInsideASurrogatePair_TruncatesOneUnitShorter()
    {
        // Truncation is the last step of the pipeline, after sanitization, so a lone surrogate cut
        // here reaches the planner's candidate basename with nothing left to repair it.
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
