using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests;

/// <summary>
/// Proves the dual MAX_PATH reduction: the filename component (≤255) AND the full generated path
/// (≤259) are BOTH enforced; over-long names drop fields in DropOrder then hard-truncate the title.
/// Driven by <see cref="LongTemplateFixture"/>, engineered to exhaust every drop and force a title
/// truncate.
/// </summary>
public class LengthReducerTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FixtureMulti =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = LongTemplateFixture.Performers,
            ["tags"] = LongTemplateFixture.Tags,
        };

    // ---- FitsBoth: measures BOTH constraints (filename component AND full path) ----

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

    // ---- Control: a short name is returned unchanged (no drop, no truncate) ----

    [Fact]
    public void Fit_ShortName_ReturnedUnchanged()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "Movie", ["ext"] = "mkv" };
        var options = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "" };
        var r = TemplateEngine.Render(tokens, new Dictionary<string, IReadOnlyList<string>>(), options);
        Assert.Equal("Movie", r.Filename);
        Assert.Equal(".mkv", r.Ext);
        Assert.True(LengthReducer.FitsBoth(r.FolderPath, r.Filename, r.Ext, options));
    }

    // ---- The long fixture: drop every field, then hard-truncate the title ----

    [Fact]
    public void Fit_LongFixture_SatisfiesBothCaps()
    {
        var options = new RenamerOptions { FilenameTemplate = LongTemplateFixture.FilenameTemplate, FolderTemplate = "" };
        var r = TemplateEngine.Render(LongTemplateFixture.Tokens, FixtureMulti, options);

        Assert.True(r.Filename.Length + r.Ext.Length <= options.FilenameMax,
            $"filename component {r.Filename.Length + r.Ext.Length} > {options.FilenameMax}");
        int sep = r.FolderPath.Length > 0 ? 1 : 0;
        int full = r.FolderPath.Length + sep + r.Filename.Length + r.Ext.Length;
        Assert.True(full <= options.FullPathMax, $"full path {full} > {options.FullPathMax}");
        Assert.True(LengthReducer.FitsBoth(r.FolderPath, r.Filename, r.Ext, options));
    }

    [Fact]
    public void Fit_LongFixture_DroppedFieldsAreAbsent()
    {
        var options = new RenamerOptions { FilenameTemplate = LongTemplateFixture.FilenameTemplate, FolderTemplate = "" };
        var r = TemplateEngine.Render(LongTemplateFixture.Tokens, FixtureMulti, options);

        // The early drop-order fields' values must be gone from the final name.
        Assert.DoesNotContain(LongTemplateFixture.Tokens["videoCodec"], r.Filename);
        Assert.DoesNotContain(LongTemplateFixture.Tokens["audioCodec"], r.Filename);
        Assert.DoesNotContain(LongTemplateFixture.Tokens["frameRate"], r.Filename);
    }

    [Fact]
    public void Fit_LongFixture_TitleHardTruncated()
    {
        var options = new RenamerOptions { FilenameTemplate = LongTemplateFixture.FilenameTemplate, FolderTemplate = "" };
        var r = TemplateEngine.Render(LongTemplateFixture.Tokens, FixtureMulti, options);

        // The full long title (200+ chars) cannot survive intact within a 255-char filename.
        Assert.DoesNotContain(LongTemplateFixture.LongTitle, r.Filename);
        Assert.True(r.Filename.Length < LongTemplateFixture.LongTitle.Length);
    }

    // ---- Deep-folder case: a short filename in a deep folder is still caught by the full-path cap ----

    // ---- A2: the engine surfaces the actually-dropped fields (truthful, not diffed) ----

    [Fact]
    public void RenderWithDropped_ShortName_DropsNothing()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "Movie", ["ext"] = "mkv" };
        var options = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "" };

        var (result, dropped) = TemplateEngine.RenderWithDropped(
            tokens, new Dictionary<string, IReadOnlyList<string>>(), options);

        Assert.Equal("Movie", result.Filename);
        Assert.Empty(dropped); // fit both caps without dropping anything
    }

    [Fact]
    public void RenderWithDropped_LongFixture_NamesDroppedFieldsInDropOrder()
    {
        var options = new RenamerOptions { FilenameTemplate = LongTemplateFixture.FilenameTemplate, FolderTemplate = "" };

        var (_, dropped) = TemplateEngine.RenderWithDropped(LongTemplateFixture.Tokens, FixtureMulti, options);

        // The fixture is engineered to exhaust every drop-order field, so the dropped set is the
        // full DropOrder in order — proves the names come from the reducer, not a string diff.
        Assert.Equal(options.DropOrder, dropped);
    }

    [Fact]
    public void FitWithDropped_ShortName_EmptyDropped_AndDelegatingFitMatches()
    {
        var o = new RenamerOptions();
        (string folder, string name) ReRender(IReadOnlyCollection<string> _) => ("", "name");

        var (result, dropped) = LengthReducer.FitWithDropped("", "name", ".mkv", o, ReRender);
        var fitOnly = LengthReducer.Fit("", "name", ".mkv", o, ReRender);

        Assert.Empty(dropped);
        Assert.Equal(fitOnly, result); // Fit delegates to FitWithDropped — same RenamerResult
    }

    [Fact]
    public void Fit_ShortNameDeepFolder_ReducesToFitFullPath()
    {
        // A short title but a folder template that renders very deep -> full path over 259.
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
        Assert.True(LengthReducer.FitsBoth(r.FolderPath, r.Filename, r.Ext, options));
    }
}
