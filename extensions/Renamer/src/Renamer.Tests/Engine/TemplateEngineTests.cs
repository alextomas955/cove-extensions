using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests.Engine;

public class TemplateEngineTests
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

    [Fact]
    public void CoreTokens_ScalarTokens_ResolveFromDict()
    {
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "My Movie",
            ["studio"] = "Acme",
            ["studioCode"] = "AC-001",
            ["date"] = "2026-06-27",
            ["year"] = "2026",
        };
        var r = Render("$studio $studioCode $title $date $year", tokens);
        Assert.Equal("Acme AC-001 My Movie 2026-06-27 2026", r.Filename);
    }

    [Fact]
    public void CoreTokens_CaseInsensitiveLookup()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "Hello" };
        var r = Render("$TITLE", tokens);
        Assert.Equal("Hello", r.Filename);
    }

    [Fact]
    public void CoreTokens_UnknownToken_ResolvesEmpty()
    {
        var r = Render("a$nope-b", NoTokens);
        Assert.Equal("a-b", r.Filename);
    }

    [Fact]
    public void CoreTokens_Performers_FromMultiValueSideInput()
    {
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Bob", "Alice" },
        };
        var r = Render("$performers", multi: multi);
        // default Performers sort = NameAsc, separator = " " (default; the "," in a comma separator
        // would be stripped anyway by the default RemoveCharacters = ",#").
        Assert.Equal("Alice Bob", r.Filename);
    }

    [Fact]
    public void CoreTokens_Tags_FromMultiValueSideInput()
    {
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["tags"] = new[] { "hd", "action" },
        };
        var r = Render("$tags", multi: multi);
        // default Tags separator = " "
        Assert.Equal("action hd", r.Filename);
    }

    [Fact]
    public void CoreTokens_Ext_AppendedNotDuplicated()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "Movie", ["ext"] = "mkv" };
        var r = Render("$title.$ext", tokens);
        Assert.Equal("Movie", r.Filename);
        Assert.Equal(".mkv", r.Ext);
    }

    [Fact]
    public void CoreTokens_DollarEscape_LiteralDollar()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "X" };
        var r = Render("$$ $title", tokens);
        Assert.Equal("$ X", r.Filename);
    }

    [Fact]
    public void OptionalGroup_AllEmpty_RemovesEntireSpanIncludingInnerLiterals()
    {
        var tokens = new Dictionary<string, string> { ["studio"] = "Acme" };
        var r = Render("$studio{ - $performers}", tokens);
        Assert.Equal("Acme", r.Filename);
    }

    [Fact]
    public void OptionalGroup_AllEmpty_NoDanglingSeparator_PrefixForm()
    {
        var r = Render("prefix {$performers}", NoTokens);
        Assert.Equal("prefix", r.Filename); // trailing space trimmed by sanitizer
    }

    [Fact]
    public void OptionalGroup_GroupWithSeparatorBothEmpty_Disappears()
    {
        var r = Render("{$studio - $performers}", NoTokens);
        Assert.Equal("", r.Filename);
    }

    [Fact]
    public void OptionalGroup_NonEmpty_RendersWithInnerLiteralKept()
    {
        var tokens = new Dictionary<string, string> { ["studio"] = "Acme" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Alice" },
        };
        var r = Render("{$studio - $performers}", tokens, multi);
        Assert.Equal("Acme - Alice", r.Filename);
    }

    [Fact]
    public void OptionalGroup_OneEmptyOneNonEmpty_RendersWithEmptyCollapsed()
    {
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Alice" },
        };
        var r = Render("{$studio$performers}", NoTokens, multi);
        Assert.Equal("Alice", r.Filename);
    }

    [Fact]
    public void OptionalGroup_UnclosedBrace_RendersAsAGroup()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "X" };
        var withStudio = new Dictionary<string, string>(tokens) { ["studio"] = "Acme" };

        Assert.Equal("X", Render("$title {- $studio", tokens).Filename);
        Assert.Equal("X - Acme", Render("$title {- $studio", withStudio).Filename);
    }

    [Theory]
    [InlineData("2026-03-12", "1080", "2026-03-12 - Title [1080p]")]
    [InlineData("", "1080", "Title [1080p]")]
    [InlineData("2026-03-12", "", "2026-03-12 - Title")]
    [InlineData("", "", "Title")]
    public void GroupedTemplate_DegradesCleanly(string date, string height, string expected)
    {
        var tokens = new Dictionary<string, string> { ["title"] = "Title" };
        if (date.Length > 0)
        {
            tokens["date"] = date;
        }

        if (height.Length > 0)
        {
            tokens["height"] = height;
        }

        var r = Render("{$date - }$title{ [$height]}", tokens);

        Assert.Equal(expected, r.Filename);
    }

    [Fact]
    public void FolderTemplate_KeepsSlashAsSeparator()
    {
        var tokens = new Dictionary<string, string> { ["studio"] = "Acme", ["year"] = "2026", ["title"] = "Movie" };
        var r = Render("$title", tokens, folder: "$studio/$year");
        Assert.Equal("Acme/2026", r.FolderPath);
        Assert.Equal("Movie", r.Filename);
    }

    [Fact]
    public void FolderTemplate_FilenameStripsSlash()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "a/b" };
        var r = Render("$title", tokens);
        Assert.Equal("ab", r.Filename);
    }

    [Fact]
    public void FolderTemplate_Empty_NoFolderMove()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "Movie" };
        var r = Render("$title", tokens, folder: "");
        Assert.Equal("", r.FolderPath);
    }

    [Fact]
    public void FolderTemplate_EachSegmentSanitizedIndependently()
    {
        var tokens = new Dictionary<string, string> { ["studio"] = "Ac:me", ["year"] = "2026" };
        var r = Render("x", tokens, folder: "$studio/$year");
        Assert.Equal("Acme/2026", r.FolderPath);
    }

    [Theory]
    [InlineData("G:/media/$studio", "G/media/Acme")]
    [InlineData("/srv/media/$studio", "srv/media/Acme")]
    [InlineData("//nas/share/$studio", "nas/share/Acme")]
    public void FolderTemplate_ThatLooksRooted_StillRendersRelative(string template, string expected)
    {
        // A destination names its root by choosing one of Cove's library paths, so a template never
        // does: the per-segment sanitizer strips ':' and empty segments drop, which leaves a typed root
        // as ordinary folder names under whichever root the destination chose.
        var tokens = new Dictionary<string, string> { ["studio"] = "Acme" };

        Assert.Equal(expected, Render("x", tokens, folder: template).FolderPath);
    }

    [Fact]
    public void FolderTemplate_ATokenValue_CannotMakeTheDestinationAbsolute()
    {
        // A studio name is metadata, and metadata choosing a drive would be a path injection. The value
        // is sanitized into one ordinary segment.
        var tokens = new Dictionary<string, string> { ["studio"] = @"C:\Windows" };

        Assert.Equal("Archive/CWindows", Render("x", tokens, folder: "Archive/$studio").FolderPath);
    }

    [Fact]
    public void DefaultOptions_RenderTheShippedTemplates()
    {
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "The Movie",
            ["studio"] = "Acme Studio",
            ["date"] = "2026-06-27",
            ["width"] = "1920",
            ["height"] = "1080",
            ["ext"] = "mkv",
        };

        var r = TemplateEngine.Render(tokens, NoMulti, new RenamerOptions());

        Assert.Equal("2026-06-27 - The Movie [1080p]", r.Filename);
        Assert.Equal("", r.FolderPath);
        Assert.Equal(".mkv", r.Ext);
    }

    [Fact]
    public void FieldRewrites_FlowThroughRender_TitleArticleAndStudioSqueeze()
    {
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "The Matrix",
            ["studio"] = "Reality Kings",
        };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "$studio",
            StripLeadingArticles = true,
            SqueezeStudioNames = true,
        };

        var r = TemplateEngine.Render(tokens, NoMulti, o);

        Assert.Equal("Matrix", r.Filename);
        Assert.Equal("RealityKings", r.FolderPath);
    }

    [Fact]
    public void PreventTitlePerformer_DropsNameInTitle_FreesOverflowSlot()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "Eve Goes Home" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Eve", "Bob", "Carol" },
        };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$performers",
            PreventTitlePerformer = true,
            RemoveCharacters = "", // keep the comma separator (default ",#" would strip it)
            Performers = new MultiValueOptions
            {
                Separator = ", ",
                MaxCount = 2,
                OnOverflow = OverflowPolicy.KeepFirst,
                Sort = SortOrder.None,
            },
        };

        var r = TemplateEngine.Render(tokens, multi, o);

        Assert.Equal("Bob, Carol", r.Filename);
    }

    [Fact]
    public void PreventTitlePerformer_ComparesAgainstRewrittenTitle()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "The Eve" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Eve", "Bob" },
        };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$performers",
            PreventTitlePerformer = true,
            StripLeadingArticles = true,
            RemoveCharacters = "", // keep the comma separator (default ",#" would strip it)
            Performers = new MultiValueOptions { Separator = ", ", Sort = SortOrder.None },
        };

        var r = TemplateEngine.Render(tokens, multi, o);

        Assert.Equal("Bob", r.Filename);
    }

    [Fact]
    public void PreventConsecutive_CollapsesConsecutiveFolderSegments()
    {
        var tokens = new Dictionary<string, string> { ["studio"] = "Foo", ["title"] = "X" };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "$studio/$studio/Bar",
            PreventConsecutiveSegments = true,
        };

        var r = TemplateEngine.Render(tokens, NoMulti, o);

        Assert.Equal("Foo/Bar", r.FolderPath);
    }

    [Fact]
    public void PreventConsecutive_DoesNotCollapse_InFilename()
    {
        // A filename is one segment with no '/', so the duplicate text is not collapsed.
        var tokens = new Dictionary<string, string> { ["studio"] = "Foo" };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$studio $studio",
            PreventConsecutiveSegments = true,
        };

        var r = TemplateEngine.Render(tokens, NoMulti, o);

        Assert.Equal("Foo Foo", r.Filename);
    }

    [Fact]
    public void Performers_RecordOrdering_AppliesBeforeTheLimit_EndToEnd()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "Film" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "alice", "Bob", "Charlie" },
        };
        var records = new[]
        {
            new RenamerPerformer(1, "alice", "Female"),
            new RenamerPerformer(2, "Bob", "Male"),
            new RenamerPerformer(3, "Charlie", "Male"),
        };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$performers",
            RemoveCharacters = "", // keep the comma separator (default ",#" would strip it)
            Performers = new MultiValueOptions
            {
                Separator = ", ",
                Sort = SortOrder.NameAsc,
                MaxCount = 2,
                OnOverflow = OverflowPolicy.KeepFirst,
                IgnoreGenders = ["Female"],
            },
        };

        var r = TemplateEngine.Render(tokens, multi, o, performers: records);

        Assert.Equal("Bob, Charlie", r.Filename);
    }

    [Fact]
    public void Performers_RecordPath_DefaultOptions_RendersSameNamesAsNamePath()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "Film" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Charlie", "alice", "Bob" },
        };
        var records = new[]
        {
            new RenamerPerformer(3, "Charlie", "Male"),
            new RenamerPerformer(1, "alice", "Female"),
            new RenamerPerformer(2, "Bob", "Male"),
        };
        var o = new RenamerOptions { FilenameTemplate = "$performers" };

        var withRecords = TemplateEngine.Render(tokens, multi, o, performers: records);
        var nameOnly = TemplateEngine.Render(tokens, multi, o);

        Assert.Equal(nameOnly.Filename, withRecords.Filename);
    }

    [Fact]
    public void Performers_RecordPath_DropsTitlePerformer_ThenOrders()
    {
        // The title performer drops by name before the limit, and the id sort then orders only the
        // survivors. Id order differs from name order, so the name-only list would render "Bob, Carol".
        var tokens = new Dictionary<string, string> { ["title"] = "Eve Goes Home" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Eve", "Bob", "Carol" },
        };
        var records = new[]
        {
            new RenamerPerformer(1, "Eve", "Female"),
            new RenamerPerformer(3, "Bob", "Male"),
            new RenamerPerformer(2, "Carol", "Female"),
        };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$performers",
            PreventTitlePerformer = true,
            RemoveCharacters = "", // keep the comma separator (default ",#" would strip it)
            Performers = new MultiValueOptions
            {
                Separator = ", ",
                Sort = SortOrder.IdAsc,
                MaxCount = 2,
                OnOverflow = OverflowPolicy.KeepFirst,
            },
        };

        var r = TemplateEngine.Render(tokens, multi, o, performers: records);

        Assert.Equal("Carol, Bob", r.Filename);
    }

    [Theory]
    [InlineData("It’s Here", "It's Here")]
    // The folded straight double-quote is OS-illegal, so the clean step removes it afterwards.
    [InlineData("“Hi”", "Hi")]
    [InlineData("A–B", "A-B")]
    // Mid-title, so the folded dots are not trimmed as trailing-edge dots.
    [InlineData("Wait… What", "Wait... What")]
    public void NormalizePunctuation_OnByDefault_FoldsBeforeTheClean(string title, string expected)
    {
        var r = Render("$title", new Dictionary<string, string> { ["title"] = title });
        Assert.Equal(expected, r.Filename);
    }

    [Fact]
    public void NormalizePunctuation_Off_PassesTypographicCharsThrough()
    {
        var o = new RenamerOptions { NormalizePunctuation = false };
        var r = Render("$title", new Dictionary<string, string> { ["title"] = "It’s A–Z" }, options: o);
        Assert.Equal("It’s A–Z", r.Filename);
    }
}
