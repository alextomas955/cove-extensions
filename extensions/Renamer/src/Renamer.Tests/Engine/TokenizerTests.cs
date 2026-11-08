using Renamer.Engine;

namespace Renamer.Tests.Engine;

public class TokenizerTests
{
    [Theory]
    [MemberData(nameof(Templates))]
    public void Scan_SplitsTheTemplateIntoSegments(string template, Segment[] expected)
    {
        Assert.Equal(expected, Tokenizer.Scan(template));
    }

    public static TheoryData<string, Segment[]> Templates() => new()
    {
        { "$title", [new Segment(SegKind.Token, "title")] },
        {
            "a $title b",
            [
                new Segment(SegKind.Literal, "a "),
                new Segment(SegKind.Token, "title"),
                new Segment(SegKind.Literal, " b"),
            ]
        },
        { "$$", [new Segment(SegKind.Literal, "$")] },
        {
            "$$title",
            [new Segment(SegKind.Literal, "$"), new Segment(SegKind.Token, "title")]
        },
        { "cost $ each", [new Segment(SegKind.Literal, "cost $ each")] },
        {
            // '-' is not a name char, so the token name is "studio".
            "$studio-x",
            [new Segment(SegKind.Token, "studio"), new Segment(SegKind.Literal, "-x")]
        },
        { "$studio_Code2", [new Segment(SegKind.Token, "studio_Code2")] },
        {
            "$studio - {$performers}",
            [
                new Segment(SegKind.Token, "studio"),
                new Segment(SegKind.Literal, " - "),
                new Segment(SegKind.GroupOpen, "{"),
                new Segment(SegKind.Token, "performers"),
                new Segment(SegKind.GroupClose, "}"),
            ]
        },
        { "a}b", [new Segment(SegKind.Literal, "a}b")] },
        {
            "$title {$studio",
            [
                new Segment(SegKind.Token, "title"),
                new Segment(SegKind.Literal, " "),
                new Segment(SegKind.GroupOpen, "{"),
                new Segment(SegKind.Token, "studio"),
            ]
        },
    };

    [Fact]
    public void Adversarial_ManyOpenBraces_EmitOneGroupOpenEach()
    {
        const int braces = 100_000;

        var segs = Tokenizer.Scan(new string('{', braces));

        Assert.Equal(braces, segs.Count);
        Assert.All(segs, s => Assert.Equal(SegKind.GroupOpen, s.Kind));
    }

    [Fact]
    public void Empty_Template_ProducesNoSegments()
    {
        Assert.Empty(Tokenizer.Scan(""));
    }
}
