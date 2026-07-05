using Renamer.Engine;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests;

/// <summary>
/// Drives the <see cref="TemplateEngine.Render"/> orchestrator: full core token set
/// (CoreTokens*), {} optional-group collapse incl. no-dangling-punctuation (OptionalGroup*),
/// and independent filename/folder rendering with correct '/' handling (FolderTemplate*).
/// </summary>
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

    // ---- CoreTokens: full token set resolution ----

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
        Assert.Equal("a-b", r.Filename); // unknown token -> empty
    }

    [Fact]
    public void CoreTokens_Resolution_DerivedFromHeight()
    {
        var tokens = new Dictionary<string, string> { ["height"] = "2160", ["title"] = "X" };
        var r = Render("$title $resolution", tokens);
        Assert.Equal("X 4K", r.Filename);
    }

    [Theory]
    [InlineData("2160", "4K")]
    [InlineData("1440", "1440p")]
    [InlineData("1080", "1080p")]
    [InlineData("720", "720p")]
    [InlineData("480", "480p")]
    public void CoreTokens_ResolutionBuckets(string height, string label)
    {
        var tokens = new Dictionary<string, string> { ["height"] = height };
        var r = Render("$resolution", tokens);
        Assert.Equal(label, r.Filename);
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

    // ---- OptionalGroup: {} collapse ----

    [Fact]
    public void OptionalGroup_AllEmpty_RemovesEntireSpanIncludingInnerLiterals()
    {
        var tokens = new Dictionary<string, string> { ["studio"] = "Acme" };
        // performers empty -> the whole {...} (incl. " - " inside) is removed; no dangling separator.
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
        // studio empty, performers present -> group renders; the empty studio token contributes
        // nothing but the inner literals stay (the engine collapses empties).
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Alice" },
        };
        var r = Render("{$studio$performers}", NoTokens, multi);
        Assert.Equal("Alice", r.Filename);
    }

    [Fact]
    public void OptionalGroup_UnbalancedBrace_DoesNotThrow()
    {
        var tokens = new Dictionary<string, string> { ["title"] = "X" };
        var ex = Record.Exception(() => Render("$title {$studio", tokens));
        Assert.Null(ex);
    }

    // ---- default grouped template: {$date - }$title{ [$height]} degradation ----

    [Theory]
    [InlineData("2026-03-12", "1080", "2026-03-12 - Title [1080]")] // both groups render
    [InlineData("", "1080", "Title [1080]")]                        // date-less: no leading " - "
    [InlineData("2026-03-12", "", "2026-03-12 - Title")]            // height-less: no empty brackets
    [InlineData("", "", "Title")]                                   // bare title only
    public void DefaultGroupedTemplate_DegradesCleanly(string date, string height, string expected)
    {
        // $height is the RAW numeric token (1080), distinct from the derived $resolution bucket
        // (which would render "1080p"); a date/height absent from the dict resolves empty so its
        // {} group collapses without leaving a dangling separator or empty brackets.
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

    // ---- FolderTemplate: independent rendering, '/' handling ----

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
        Assert.Equal("ab", r.Filename); // '/' is illegal in a filename segment -> stripped
    }

    [Fact]
    public void FolderTemplate_RendersIndependentlyOfFilename()
    {
        var tokens = new Dictionary<string, string> { ["studio"] = "Acme", ["title"] = "Movie" };
        var r = Render("$title", tokens, folder: "$studio");
        Assert.Equal("Acme", r.FolderPath);
        Assert.Equal("Movie", r.Filename);
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
        // ':' illegal stripped per-segment, '/' kept as separator
        Assert.Equal("Acme/2026", r.FolderPath);
    }

    // ---- FIELD-01: squeeze_studio_names (engine) ----

    [Fact]
    public void Squeeze_TwoStudioVariants_RenderToOneStableFolder()
    {
        // Regression through the full engine: both spacing variants render one folder key.
        var o = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "$studio",
            SqueezeStudioNames = true,
        };

        var spaced = TemplateEngine.Render(
            new Dictionary<string, string> { ["studio"] = "Reality Kings", ["title"] = "X" }, NoMulti, o);
        var squeezed = TemplateEngine.Render(
            new Dictionary<string, string> { ["studio"] = "RealityKings", ["title"] = "X" }, NoMulti, o);

        Assert.Equal("RealityKings", spaced.FolderPath);
        Assert.Equal("RealityKings", squeezed.FolderPath);
    }

    // ---- additive / non-breaking guarantee ----

    [Fact]
    public void DefaultOptions_RenderByteIdentical_ToPrePhaseEngine()
    {
        // GATE: with NO field-rewrite settings, output must be byte-identical to the v1.3 engine.
        // Expected values are the literal strings the engine produced before this phase.
        var tokens = new Dictionary<string, string>
        {
            ["title"] = "The Movie",
            ["studio"] = "Acme Studio",
            ["year"] = "2026",
            ["height"] = "1080",
        };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$title{ [$resolution]}",
            FolderTemplate = "$studio/$year",
        };

        var r = TemplateEngine.Render(tokens, NoMulti, o);

        Assert.Equal("The Movie [1080p]", r.Filename);
        Assert.Equal("Acme Studio/2026", r.FolderPath);
        Assert.Equal("", r.Ext);
    }

    // ---- field rewrites end-to-end ----

    [Fact]
    public void FieldRewrites_FlowThroughRender_TitleArticleAndStudioSqueeze()
    {
        // Combined: FIELD-03 strips the leading article from $title and FIELD-01 squeezes
        // the $studio spaces — both flow through the existing BuildResolvedMap -> render ->
        // sanitize pipeline via the extended RewriteScalar (no new wiring).
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

    // ---- FIELD-05: prevent_title_performer (engine) ----

    [Fact]
    public void PreventTitlePerformer_DropsNameInTitle_FreesOverflowSlot()
    {
        // Eve is named in the title -> dropped from the RAW performers list BEFORE the MaxCount cap,
        // so the cap (2, KeepFirst) now admits Carol: a dropped name freed an overflow slot.
        var tokens = new Dictionary<string, string> { ["title"] = "Eve Goes Home" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Eve", "Bob", "Carol" },
        };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$performers",
            PreventTitlePerformer = true,
            // This test exercises the comma-separated join; opt out of the default RemoveCharacters
            // (",#") so the separator comma is not stripped by the final sanitize pass.
            RemoveCharacters = "",
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
        // FIELD-03 strips the leading article: "The Eve" -> "Eve" BEFORE FIELD-05 compares,
        // so Eve (now a whole word in the rewritten title) is dropped.
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

    // ---- FIELD-06: prevent_consecutive (engine) ----

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

        Assert.Equal("Foo/Bar", r.FolderPath); // Foo/Foo/Bar -> Foo/Bar
    }

    [Fact]
    public void PreventConsecutive_LeavesNonConsecutiveFolderSegments()
    {
        var tokens = new Dictionary<string, string> { ["studio"] = "Foo", ["year"] = "Bar", ["title"] = "X" };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$title",
            FolderTemplate = "$studio/$year/$studio",
            PreventConsecutiveSegments = true,
        };

        var r = TemplateEngine.Render(tokens, NoMulti, o);

        Assert.Equal("Foo/Bar/Foo", r.FolderPath); // non-consecutive untouched
    }

    [Fact]
    public void PreventConsecutive_DoesNotCollapse_InFilename()
    {
        // A filename is one segment with no '/', so the duplicate text is NOT collapsed.
        var tokens = new Dictionary<string, string> { ["studio"] = "Foo" };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$studio $studio",
            PreventConsecutiveSegments = true,
        };

        var r = TemplateEngine.Render(tokens, NoMulti, o);

        Assert.Equal("Foo Foo", r.Filename); // filename untouched
    }

    // ---- Performer ordering through Render (records carried alongside the name side-input) ----

    [Fact]
    public void Performers_RecordOrdering_AppliesBeforeTheLimit_EndToEnd()
    {
        // A gender ignore should drop a performer BEFORE the max-count limit, so two non-ignored
        // performers survive even though there were three candidates and the limit is two.
        var tokens = new Dictionary<string, string> { ["title"] = "Film" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "alice", "Bob", "Charlie" },
        };
        var records = new[]
        {
            new RenamerPerformer(1, "alice", false, "Female"),
            new RenamerPerformer(2, "Bob", false, "Male"),
            new RenamerPerformer(3, "Charlie", false, "Male"),
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
        // Regression guard: feeding the records with default performer options renders the same
        // joined names as the name-only path (no records) would.
        var tokens = new Dictionary<string, string> { ["title"] = "Film" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Charlie", "alice", "Bob" },
        };
        var records = new[]
        {
            new RenamerPerformer(3, "Charlie", false, "Male"),
            new RenamerPerformer(1, "alice", true, "Female"),
            new RenamerPerformer(2, "Bob", false, "Male"),
        };
        var o = new RenamerOptions { FilenameTemplate = "$performers" };

        var withRecords = TemplateEngine.Render(tokens, multi, o, performers: records);
        var nameOnly = TemplateEngine.Render(tokens, multi, o);

        Assert.Equal(nameOnly.Filename, withRecords.Filename);
    }

    [Fact]
    public void Performers_RecordPath_DropsTitlePerformer_ThenOrders()
    {
        // FIELD-05 still drops a performer named in the title (by name) BEFORE the limit, and the
        // record ordering then operates only on the survivors.
        var tokens = new Dictionary<string, string> { ["title"] = "Eve Goes Home" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Eve", "Bob", "Carol" },
        };
        var records = new[]
        {
            new RenamerPerformer(1, "Eve", false, "Female"),
            new RenamerPerformer(2, "Bob", false, "Male"),
            new RenamerPerformer(3, "Carol", false, "Female"),
        };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$performers",
            PreventTitlePerformer = true,
            RemoveCharacters = "", // keep the comma separator (default ",#" would strip it)
            Performers = new MultiValueOptions
            {
                Separator = ", ",
                Sort = SortOrder.NameAsc,
                MaxCount = 2,
                OnOverflow = OverflowPolicy.KeepFirst,
            },
        };

        var r = TemplateEngine.Render(tokens, multi, o, performers: records);

        // Eve is dropped (named in the title), leaving Bob + Carol, name-ordered.
        Assert.Equal("Bob, Carol", r.Filename);
    }

    [Fact]
    public void Performers_RecordPath_DuplicateName_KeepsBothWhenNeitherInTitle()
    {
        // Two distinct performers share the name "Alex" (the DB does not enforce unique performer
        // names). When neither is named in the title, BOTH survive the drop and both render — the
        // record channel preserves per-position multiplicity rather than collapsing duplicates by name.
        var tokens = new Dictionary<string, string> { ["title"] = "Bob Goes Home" };
        var multi = new Dictionary<string, IReadOnlyList<string>>
        {
            ["performers"] = new[] { "Alex", "Alex", "Bob" },
        };
        var records = new[]
        {
            new RenamerPerformer(1, "Alex", false, "Female"),
            new RenamerPerformer(2, "Alex", false, "Male"),
            new RenamerPerformer(3, "Bob", false, "Male"),
        };
        var o = new RenamerOptions
        {
            FilenameTemplate = "$performers",
            PreventTitlePerformer = true,
            RemoveCharacters = "", // keep the comma separator (default ",#" would strip it)
            Performers = new MultiValueOptions { Separator = ", ", Sort = SortOrder.None },
        };

        var r = TemplateEngine.Render(tokens, multi, o, performers: records);

        // Bob is dropped (named in the title); both Alex records survive, in order.
        Assert.Equal("Alex, Alex", r.Filename);
    }

    [Fact]
    public void DropPerformersInTitleRecords_DropsOnlyTitleMatchedPositions_KeepsDuplicates()
    {
        // Per-position proof: filtering the records (not a name-keyed set) keeps a surviving duplicate.
        // Two performers named "Alex" plus one "Eve"; only "Eve" is in the title, so both Alex records
        // remain — exactly the case a name-keyed all-or-nothing rejoin could not express.
        var records = new[]
        {
            new RenamerPerformer(1, "Alex", false, "Female"),
            new RenamerPerformer(2, "Alex", false, "Male"),
            new RenamerPerformer(3, "Eve", false, "Female"),
        };
        var o = new RenamerOptions { PreventTitlePerformer = true };

        var survivors = FieldRewriter.DropPerformersInTitle(records, "Eve Goes Home", o);

        Assert.Equal([1, 2], survivors.Select(p => p.Id).ToArray());
    }
}
