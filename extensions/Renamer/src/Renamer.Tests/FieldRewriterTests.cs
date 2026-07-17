using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests;

public class FieldRewriterTests
{
    // ---- FIELD-01: squeeze_studio_names ----

    [Fact]
    public void Squeeze_TwoSpacingVariants_ProduceOneIdenticalStudioKey()
    {
        // Canonical regression: two spacing variants of one studio must collapse to ONE key.
        var o = new RenamerOptions { SqueezeStudioNames = true };
        Assert.Equal("RealityKings", FieldRewriter.RewriteScalar(Tokens.Studio, "Reality Kings", o));
        Assert.Equal("RealityKings", FieldRewriter.RewriteScalar(Tokens.Studio, "RealityKings", o));
    }

    [Fact]
    public void Squeeze_RemovesAllSpaces_NotJustCollapse()
    {
        var o = new RenamerOptions { SqueezeStudioNames = true };
        Assert.Equal("ABC", FieldRewriter.RewriteScalar(Tokens.Studio, "A B C", o));
    }

    [Fact]
    public void Squeeze_TargetsStudioOnly_TitleUntouched()
    {
        var o = new RenamerOptions { SqueezeStudioNames = true };
        Assert.Equal("Reality Kings", FieldRewriter.RewriteScalar(Tokens.Title, "Reality Kings", o));
    }

    [Fact]
    public void Squeeze_CaseInsensitiveTokenName()
    {
        var o = new RenamerOptions { SqueezeStudioNames = true };
        // Token name compared case-insensitively against Tokens.Studio.
        Assert.Equal("RealityKings", FieldRewriter.RewriteScalar("STUDIO", "Reality Kings", o));
    }

    [Fact]
    public void Squeeze_OffByDefault_StudioUnchanged()
    {
        var o = new RenamerOptions(); // SqueezeStudioNames defaults false
        Assert.Equal("Reality Kings", FieldRewriter.RewriteScalar(Tokens.Studio, "Reality Kings", o));
    }

    // ---- FIELD-02: field_replacer ----

    [Fact]
    public void Replacer_StripsApostrophe_FromStudioOnly()
    {
        var o = new RenamerOptions
        {
            FieldReplacers = [new FieldReplaceRule { TargetToken = "studio", Find = "'", Replace = "" }],
        };
        Assert.Equal("Bobs Studio", FieldRewriter.RewriteScalar(Tokens.Studio, "Bob's Studio", o));
        // A title with the same apostrophe is UNTOUCHED — the rule targets studio only.
        Assert.Equal("Bob's Movie", FieldRewriter.RewriteScalar(Tokens.Title, "Bob's Movie", o));
    }

    [Fact]
    public void Replacer_IsLiteral_NotRegex()
    {
        var o = new RenamerOptions
        {
            FieldReplacers = [new FieldReplaceRule { TargetToken = "title", Find = ".", Replace = "" }],
        };
        // A literal dot replace removes only the literal '.', not "any char".
        Assert.Equal("ab", FieldRewriter.RewriteScalar(Tokens.Title, "a.b", o));
    }

    [Fact]
    public void Replacer_MultipleRules_ApplyInListOrder()
    {
        var o = new RenamerOptions
        {
            FieldReplacers =
            [
                new FieldReplaceRule { TargetToken = "title", Find = "X", Replace = "Y" },
                new FieldReplaceRule { TargetToken = "title", Find = "Y", Replace = "Z" },
            ],
        };
        // First rule X->Y, then second rule Y->Z applies to the now-Y value: X -> Z.
        Assert.Equal("Z", FieldRewriter.RewriteScalar(Tokens.Title, "X", o));
    }

    [Fact]
    public void Replacer_TargetToken_CaseInsensitive()
    {
        var o = new RenamerOptions
        {
            FieldReplacers = [new FieldReplaceRule { TargetToken = "STUDIO", Find = "'", Replace = "" }],
        };
        Assert.Equal("Bobs Studio", FieldRewriter.RewriteScalar(Tokens.Studio, "Bob's Studio", o));
    }

    [Fact]
    public void Replacer_EmptyFind_IsNoOp()
    {
        // An empty Find must not loop/throw — the rule is skipped.
        var o = new RenamerOptions
        {
            FieldReplacers = [new FieldReplaceRule { TargetToken = "title", Find = "", Replace = "X" }],
        };
        Assert.Equal("abc", FieldRewriter.RewriteScalar(Tokens.Title, "abc", o));
    }

    [Fact]
    public void Replacer_OffByDefault_ValueUnchanged()
    {
        var o = new RenamerOptions();
        Assert.Equal("Bob's Studio", FieldRewriter.RewriteScalar(Tokens.Studio, "Bob's Studio", o));
    }

    // ---- FIELD-03: prepositions_removal ----

    [Theory]
    [InlineData("The Matrix", "Matrix")]
    [InlineData("A Movie", "Movie")]
    [InlineData("An Apple", "Apple")]
    public void Article_StripsSingleLeadingArticle(string input, string expected)
    {
        var o = new RenamerOptions { StripLeadingArticles = true };
        Assert.Equal(expected, FieldRewriter.RewriteScalar(Tokens.Title, input, o));
    }

    [Fact]
    public void Article_Theatre_Untouched()
    {
        // "Theatre" starts with "The" but the next char is a letter, not whitespace.
        var o = new RenamerOptions { StripLeadingArticles = true };
        Assert.Equal("Theatre", FieldRewriter.RewriteScalar(Tokens.Title, "Theatre", o));
    }

    [Fact]
    public void Article_MidTitle_Untouched()
    {
        var o = new RenamerOptions { StripLeadingArticles = true };
        Assert.Equal("Live The Dream", FieldRewriter.RewriteScalar(Tokens.Title, "Live The Dream", o));
    }

    [Fact]
    public void Article_CaseInsensitive()
    {
        var o = new RenamerOptions { StripLeadingArticles = true };
        Assert.Equal("matrix", FieldRewriter.RewriteScalar(Tokens.Title, "the matrix", o));
    }

    [Fact]
    public void Article_StrippedAtMostOnce()
    {
        var o = new RenamerOptions { StripLeadingArticles = true };
        // Only the single leading "The" is removed, leaving "The End".
        Assert.Equal("The End", FieldRewriter.RewriteScalar(Tokens.Title, "The The End", o));
    }

    [Fact]
    public void Article_TargetsTitleOnly()
    {
        var o = new RenamerOptions { StripLeadingArticles = true };
        // $studio is not targeted by the article strip.
        Assert.Equal("The Studio", FieldRewriter.RewriteScalar(Tokens.Studio, "The Studio", o));
    }

    [Fact]
    public void Article_OffByDefault_NoOp()
    {
        var o = new RenamerOptions(); // StripLeadingArticles defaults false
        Assert.Equal("The Matrix", FieldRewriter.RewriteScalar(Tokens.Title, "The Matrix", o));
    }

    // ---- combined order ----

    [Fact]
    public void Order_FieldReplacer_RunsBefore_ArticleStrip()
    {
        // The field-replacer turns "Teh Matrix" -> "The Matrix"; the article strip then drops the
        // leading "The". If the order were reversed, "Teh" would not be an article and nothing would
        // strip.
        var o = new RenamerOptions
        {
            FieldReplacers = [new FieldReplaceRule { TargetToken = "title", Find = "Teh", Replace = "The" }],
            StripLeadingArticles = true,
        };
        Assert.Equal("Matrix", FieldRewriter.RewriteScalar(Tokens.Title, "Teh Matrix", o));
    }

    // ---- prevent_title_performer ----

    [Fact]
    public void DropPerformers_DropsWholeWordOccurrence_InTitle()
    {
        var o = new RenamerOptions { PreventTitlePerformer = true };
        var result = FieldRewriter.DropPerformersInTitle(["Eve", "Bob"], "Eve Goes Home", o);
        Assert.Equal(["Bob"], result); // Eve appears as a whole word -> dropped
    }

    [Fact]
    public void DropPerformers_KeepsName_WhenOnlyASubstring()
    {
        var o = new RenamerOptions { PreventTitlePerformer = true };
        // "Eve" is NOT a whole word in "Evelyn", so it is kept.
        var result = FieldRewriter.DropPerformersInTitle(["Eve", "Bob"], "Evelyn Goes Home", o);
        Assert.Equal(["Eve", "Bob"], result);
    }

    [Fact]
    public void DropPerformers_CaseInsensitive()
    {
        var o = new RenamerOptions { PreventTitlePerformer = true };
        // lower-case "eve" in the title still drops "Eve".
        var result = FieldRewriter.DropPerformersInTitle(["Eve", "Bob"], "the eve party", o);
        Assert.Equal(["Bob"], result);
    }

    [Fact]
    public void DropPerformers_TrimsPerformerName_BeforeMatch()
    {
        var o = new RenamerOptions { PreventTitlePerformer = true };
        // The performer name is trimmed and compared as a unit.
        var result = FieldRewriter.DropPerformersInTitle(["  Eve  ", "Bob"], "Eve Goes Home", o);
        Assert.Equal(["Bob"], result);
    }

    [Fact]
    public void DropPerformers_MatchAtTitleStartAndEnd()
    {
        var o = new RenamerOptions { PreventTitlePerformer = true };
        // Boundary at index 0 and at string end both count as whole-word matches.
        Assert.Equal(["Bob"], FieldRewriter.DropPerformersInTitle(["Eve", "Bob"], "Eve", o));
        Assert.Equal(["Bob"], FieldRewriter.DropPerformersInTitle(["Eve", "Bob"], "Home of Eve", o));
    }

    [Fact]
    public void DropPerformers_PunctuationBoundary_CountsAsWholeWord()
    {
        var o = new RenamerOptions { PreventTitlePerformer = true };
        // A non-letter-or-digit (here ',') is a word boundary.
        Assert.Equal(["Bob"], FieldRewriter.DropPerformersInTitle(["Eve", "Bob"], "Eve, Again", o));
    }

    [Fact]
    public void DropPerformers_EmptyOrWhitespaceName_NeverDropped()
    {
        // A trimmed-empty name short-circuits to a no-op (never matches everything).
        var o = new RenamerOptions { PreventTitlePerformer = true };
        var result = FieldRewriter.DropPerformersInTitle(["", "  ", "Bob"], "Any Title", o);
        Assert.Equal(["", "  ", "Bob"], result);
    }

    [Fact]
    public void DropPerformers_OffByDefault_ListUnchanged()
    {
        var o = new RenamerOptions(); // PreventTitlePerformer defaults false
        var result = FieldRewriter.DropPerformersInTitle(["Eve", "Bob"], "Eve Goes Home", o);
        Assert.Equal(["Eve", "Bob"], result); // no-op when off
    }

    // ---- FIELD-06: prevent_consecutive ----

    [Fact]
    public void CollapseConsecutive_CollapsesAdjacentDuplicates()
    {
        var o = new RenamerOptions { PreventConsecutiveSegments = true };
        Assert.Equal(["Foo", "Bar"], FieldRewriter.CollapseConsecutive(["Foo", "Foo", "Bar"], o));
    }

    [Fact]
    public void CollapseConsecutive_LeavesNonAdjacentDuplicates()
    {
        var o = new RenamerOptions { PreventConsecutiveSegments = true };
        Assert.Equal(["Foo", "Bar", "Foo"], FieldRewriter.CollapseConsecutive(["Foo", "Bar", "Foo"], o));
    }

    [Fact]
    public void CollapseConsecutive_CaseInsensitive_KeepsFirst()
    {
        var o = new RenamerOptions { PreventConsecutiveSegments = true };
        // "foo" then "Foo" -> the first occurrence is kept.
        Assert.Equal(["foo"], FieldRewriter.CollapseConsecutive(["foo", "Foo"], o));
    }

    [Fact]
    public void CollapseConsecutive_CollapsesRunsLongerThanTwo()
    {
        var o = new RenamerOptions { PreventConsecutiveSegments = true };
        Assert.Equal(["A", "B"], FieldRewriter.CollapseConsecutive(["A", "A", "A", "B"], o));
    }

    [Fact]
    public void CollapseConsecutive_Off_SegmentsUnchanged()
    {
        // Pin the off behavior explicitly: the flag now defaults on, so this case forces it off to
        // prove that with the collapse disabled, adjacent duplicate segments are left intact.
        var o = new RenamerOptions { PreventConsecutiveSegments = false };
        Assert.Equal(["Foo", "Foo", "Bar"], FieldRewriter.CollapseConsecutive(["Foo", "Foo", "Bar"], o));
    }

    [Fact]
    public void CollapseConsecutive_EmptyInput_ReturnsEmpty()
    {
        var o = new RenamerOptions { PreventConsecutiveSegments = true };
        Assert.Empty(FieldRewriter.CollapseConsecutive([], o));
    }
}
