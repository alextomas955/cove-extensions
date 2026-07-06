using Renamer.Engine;

namespace Renamer.Tests;

public class TokenizerTests
{
    [Fact]
    public void Token_SingleToken_EmitsTokenSegment()
    {
        var segs = Tokenizer.Scan("$title");
        Assert.Equal(new[] { new Segment(SegKind.Token, "title") }, segs);
    }

    [Fact]
    public void Token_SurroundingText_EmitsLiteralsAroundToken()
    {
        var segs = Tokenizer.Scan("a $title b");
        Assert.Equal(
            new[]
            {
                new Segment(SegKind.Literal, "a "),
                new Segment(SegKind.Token, "title"),
                new Segment(SegKind.Literal, " b"),
            },
            segs);
    }

    [Fact]
    public void Escape_DoubleDollar_ProducesExactlyOneLiteralDollar()
    {
        var segs = Tokenizer.Scan("$$");
        Assert.Equal(new[] { new Segment(SegKind.Literal, "$") }, segs);
    }

    [Fact]
    public void Escape_DoubleDollarThenName_ProducesLiteralDollarThenToken()
    {
        var segs = Tokenizer.Scan("$$title");
        Assert.Equal(
            new[]
            {
                new Segment(SegKind.Literal, "$"),
                new Segment(SegKind.Token, "title"),
            },
            segs);
    }

    [Fact]
    public void Escape_LoneDollarNotFollowedByName_StaysLiteral()
    {
        var segs = Tokenizer.Scan("cost $ each");
        Assert.Equal(new[] { new Segment(SegKind.Literal, "cost $ each") }, segs);
    }

    [Fact]
    public void Token_NameStopsAtFirstNonNameChar()
    {
        // '-' is not a name char, so the token name is "studio".
        var segs = Tokenizer.Scan("$studio-x");
        Assert.Equal(
            new[]
            {
                new Segment(SegKind.Token, "studio"),
                new Segment(SegKind.Literal, "-x"),
            },
            segs);
    }

    [Fact]
    public void Token_NameAllowsUnderscoreAndDigits()
    {
        var segs = Tokenizer.Scan("$studio_Code2");
        Assert.Equal(new[] { new Segment(SegKind.Token, "studio_Code2") }, segs);
    }

    [Fact]
    public void Groups_BalancedBraces_EmitGroupOpenAndClose()
    {
        var segs = Tokenizer.Scan("$studio - {$performers}");
        Assert.Equal(
            new[]
            {
                new Segment(SegKind.Token, "studio"),
                new Segment(SegKind.Literal, " - "),
                new Segment(SegKind.GroupOpen, "{"),
                new Segment(SegKind.Token, "performers"),
                new Segment(SegKind.GroupClose, "}"),
            },
            segs);
    }

    [Fact]
    public void Unbalanced_StrayCloseBrace_BecomesLiteralAndLogs()
    {
        var logs = new List<string>();
        var segs = Tokenizer.Scan("a}b", logs.Add);
        Assert.Equal(new[] { new Segment(SegKind.Literal, "a}b") }, segs);
        Assert.Single(logs);
    }

    [Fact]
    public void Unbalanced_UnclosedBrace_LogsAndDoesNotThrow()
    {
        var logs = new List<string>();
        var ex = Record.Exception(() => Tokenizer.Scan("$title {$studio", logs.Add));
        Assert.Null(ex);
        Assert.Single(logs);
    }

    [Fact]
    public void Adversarial_ManyOpenBraces_DoesNotThrowOrHang()
    {
        var template = new string('{', 100_000);
        var logs = new List<string>();
        var ex = Record.Exception(() => Tokenizer.Scan(template, logs.Add));
        Assert.Null(ex);
        Assert.Single(logs); // one summary log for the unclosed braces at EOF
    }

    [Fact]
    public void Empty_Template_ProducesNoSegments()
    {
        Assert.Empty(Tokenizer.Scan(""));
    }
}
