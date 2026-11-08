using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests.Engine;

public class FieldRewriterTests
{
    [Theory]
    [InlineData(true, Tokens.Studio, "Reality Kings", "RealityKings")]
    [InlineData(true, Tokens.Studio, "RealityKings", "RealityKings")]
    [InlineData(true, Tokens.Studio, "A B C", "ABC")]
    [InlineData(true, Tokens.Title, "Reality Kings", "Reality Kings")]
    [InlineData(true, "STUDIO", "Reality Kings", "RealityKings")]
    [InlineData(false, Tokens.Studio, "Reality Kings", "Reality Kings")]
    public void Squeeze_RemovesEverySpaceFromTheStudioToken_WhenEnabled(
        bool squeeze, string token, string input, string expected)
    {
        var o = new RenamerOptions { SqueezeStudioNames = squeeze };
        Assert.Equal(expected, FieldRewriter.RewriteScalar(token, input, o));
    }

    [Fact]
    public void Replacer_StripsApostrophe_FromStudioOnly()
    {
        var o = new RenamerOptions
        {
            FieldReplacers = [new FieldReplaceRule { TargetToken = "studio", Find = "'", Replace = "" }],
        };
        Assert.Equal("Bobs Studio", FieldRewriter.RewriteScalar(Tokens.Studio, "Bob's Studio", o));
        Assert.Equal("Bob's Movie", FieldRewriter.RewriteScalar(Tokens.Title, "Bob's Movie", o));
    }

    [Fact]
    public void Replacer_IsLiteral_NotRegex()
    {
        var o = new RenamerOptions
        {
            FieldReplacers = [new FieldReplaceRule { TargetToken = "title", Find = ".", Replace = "" }],
        };
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

    [Theory]
    [InlineData(true, Tokens.Title, "The Matrix", "Matrix")]
    [InlineData(true, Tokens.Title, "A Movie", "Movie")]
    [InlineData(true, Tokens.Title, "An Apple", "Apple")]
    [InlineData(true, Tokens.Title, "Theatre", "Theatre")]
    [InlineData(true, Tokens.Title, "Live The Dream", "Live The Dream")]
    [InlineData(true, Tokens.Title, "the matrix", "matrix")]
    [InlineData(true, Tokens.Title, "The The End", "The End")]
    [InlineData(true, Tokens.Studio, "The Studio", "The Studio")]
    [InlineData(false, Tokens.Title, "The Matrix", "The Matrix")]
    public void Article_StripsAtMostOneLeadingArticle_FromTheTitle_WhenEnabled(
        bool strip, string token, string input, string expected)
    {
        var o = new RenamerOptions { StripLeadingArticles = strip };
        Assert.Equal(expected, FieldRewriter.RewriteScalar(token, input, o));
    }

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

    [Theory]
    [InlineData(true, new[] { "Eve", "Bob" }, "Eve Goes Home", new[] { "Bob" })]
    [InlineData(true, new[] { "Eve", "Bob" }, "Evelyn Goes Home", new[] { "Eve", "Bob" })]
    [InlineData(true, new[] { "Eve", "Bob" }, "the eve party", new[] { "Bob" })]
    [InlineData(true, new[] { "  Eve  ", "Bob" }, "Eve Goes Home", new[] { "Bob" })]
    [InlineData(true, new[] { "Eve", "Bob" }, "Eve", new[] { "Bob" })]
    [InlineData(true, new[] { "Eve", "Bob" }, "Home of Eve", new[] { "Bob" })]
    [InlineData(true, new[] { "Eve", "Bob" }, "Eve, Again", new[] { "Bob" })]
    [InlineData(true, new[] { "", "  ", "Bob" }, "Any Title", new[] { "", "  ", "Bob" })]
    [InlineData(false, new[] { "Eve", "Bob" }, "Eve Goes Home", new[] { "Eve", "Bob" })]
    public void DropPerformers_DropsOnlyWholeWordTitleMatches_WhenEnabled(
        bool on, string[] names, string title, string[] expected)
    {
        var o = new RenamerOptions { PreventTitlePerformer = on };
        Assert.Equal(expected, FieldRewriter.DropPerformersInTitle(names, title, o));
    }

    [Fact]
    public void DropPerformers_Records_DropOnlyTheTitleMatchedPositions_AndKeepSameNamePerformers()
    {
        var records = new[]
        {
            new RenamerPerformer(1, "Alex", "Female"),
            new RenamerPerformer(2, "Alex", "Male"),
            new RenamerPerformer(3, "Eve", "Female"),
        };
        var o = new RenamerOptions { PreventTitlePerformer = true };

        var survivors = FieldRewriter.DropPerformersInTitle(records, "Eve Goes Home", o);

        Assert.Equal([1, 2], survivors.Select(p => p.Id).ToArray());
    }

    [Theory]
    [InlineData(true, new[] { "Foo", "Foo", "Bar" }, new[] { "Foo", "Bar" })]
    [InlineData(true, new[] { "Foo", "Bar", "Foo" }, new[] { "Foo", "Bar", "Foo" })]
    [InlineData(true, new[] { "foo", "Foo" }, new[] { "foo" })]
    [InlineData(true, new[] { "A", "A", "A", "B" }, new[] { "A", "B" })]
    [InlineData(false, new[] { "Foo", "Foo", "Bar" }, new[] { "Foo", "Foo", "Bar" })]
    public void CollapseConsecutive_CollapsesOnlyAdjacentDuplicates_WhenEnabled(
        bool on, string[] input, string[] expected)
    {
        var o = new RenamerOptions { PreventConsecutiveSegments = on };
        Assert.Equal(expected, FieldRewriter.CollapseConsecutive(input, o));
    }

    [Fact]
    public void CollapseConsecutive_EmptyInput_ReturnsEmpty()
    {
        var o = new RenamerOptions { PreventConsecutiveSegments = true };
        Assert.Empty(FieldRewriter.CollapseConsecutive([], o));
    }
}
