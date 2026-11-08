using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests.Engine;

public class SanitizerTests
{
    [Theory]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a:b")]
    [InlineData("a\"b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    public void CleanSegment_EachIllegalChar_StrippedByDefault(string input)
    {
        var o = new RenamerOptions();
        Assert.Equal("ab", Sanitizer.CleanSegment(input, o));
    }

    [Theory]
    [InlineData(null, "_", "a:b", "a_b")]
    // Control chars are dropped, not replaced (they are not in the illegal set).
    [InlineData(null, "_", "a\u0001\tb", "ab")]
    [InlineData(null, null, "a b", "a b")]
    [InlineData("_", null, "a b", "a_b")]
    [InlineData(null, null, "a    b", "a b")]
    [InlineData("_", null, "a   b", "a_b")]
    [InlineData(null, null, "  .title. ", "title")]
    [InlineData(null, null, "  ...  ", "")]
    public void CleanSegment_AppliesTheReplacements_ThenCollapsesAndTrims(
        string? spaceReplacement, string? illegalReplacement, string input, string expected)
    {
        var o = new RenamerOptions();
        if (spaceReplacement is not null)
        {
            o = o with { SpaceReplacement = spaceReplacement };
        }

        if (illegalReplacement is not null)
        {
            o = o with { IllegalReplacement = illegalReplacement };
        }

        Assert.Equal(expected, Sanitizer.CleanSegment(input, o));
    }

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("con", "con_")]
    [InlineData("Con", "Con_")]
    [InlineData("CON.mkv", "CON_.mkv")]
    [InlineData("nul.txt", "nul_.txt")]
    [InlineData("COM1.dat", "COM1_.dat")]
    [InlineData("LPT9", "LPT9_")]
    public void CleanSegment_ReservedDeviceName_GetsDisambiguatingUnderscore(string input, string expected)
    {
        var o = new RenamerOptions();
        Assert.Equal(expected, Sanitizer.CleanSegment(input, o));
    }

    [Theory]
    [InlineData("CONcert")]
    [InlineData("SCAN")]
    [InlineData("RECON")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("LPT0")]
    [InlineData("LPT10")]
    public void CleanSegment_NonReservedLookalike_LeftUnchanged(string input)
    {
        var o = new RenamerOptions();
        Assert.Equal(input, Sanitizer.CleanSegment(input, o));
    }

    [Fact]
    public void CleanSegment_TrimProducesReservedStem_GetsDisambiguatingUnderscore()
    {
        var o = new RenamerOptions();
        Assert.Equal("CON_", Sanitizer.CleanSegment("CON.", o));
    }

    [Fact]
    public void CleanSegment_IllegalStripProducesReservedStem_GetsDisambiguatingUnderscore()
    {
        var o = new RenamerOptions();
        Assert.Equal("CON_", Sanitizer.CleanSegment("CON:", o));
    }

    [Fact]
    public void CleanSegment_RemoveSet_DropsEveryListedChar()
    {
        var o = new RenamerOptions { RemoveCharacters = ",#" };
        Assert.Equal("abc", Sanitizer.CleanSegment("a,b#c", o));
    }

    [Theory]
    [InlineData("a,b#c", "a,b#c")]
    [InlineData("plain title", "plain title")]
    [InlineData("a:b", "ab")]
    [InlineData("a    b", "a b")]
    [InlineData("  .title. ", "title")]
    public void CleanSegment_RemoveSet_Empty_RemovesNothing_AndLeavesTheOtherStepsOn(
        string input, string expected)
    {
        var o = new RenamerOptions { RemoveCharacters = "" };
        Assert.Equal(expected, Sanitizer.CleanSegment(input, o));
    }

    [Fact]
    public void CleanSegment_RemoveSet_DropsBeforeIllegalReplacement()
    {
        var o = new RenamerOptions { RemoveCharacters = ":", IllegalReplacement = "_" };
        Assert.Equal("ab", Sanitizer.CleanSegment("a:b", o));
    }

    [Fact]
    public void CleanSegment_RemoveSet_ComposesWithSpaceReplacement()
    {
        var o = new RenamerOptions { RemoveCharacters = ",", SpaceReplacement = "_" };
        Assert.Equal("a_b_c", Sanitizer.CleanSegment("a, b c", o));
    }

    [Theory]
    [InlineData("Hello World", CaseTransform.None, "Hello World")]
    [InlineData("Hello World", CaseTransform.Lower, "hello world")]
    [InlineData("hello world", CaseTransform.Title, "Hello World")]
    public void Transform_ApplyCase_AppliesEachTransform(
        string input, CaseTransform transform, string expected)
    {
        Assert.Equal(expected, Sanitizer.ApplyCase(input, transform));
    }

    [Theory]
    [InlineData("Beyoncé", "Beyonce")]
    [InlineData("España", "Espana")]
    // Cyrillic must not be emptied - transliteration folds diacritics only, not whole scripts, so
    // with no diacritics to fold the whole script survives unchanged.
    [InlineData("Москва", "Москва")]
    [InlineData("Plain Title", "Plain Title")]
    public void Transform_Transliterate_FoldsDiacriticsAndLeavesEverythingElse(
        string input, string expected)
    {
        Assert.Equal(expected, Sanitizer.Transliterate(input));
    }

    [Theory]
    [InlineData("It‘s", "It's")]        // U+2018 left single quote
    [InlineData("It’s", "It's")]        // U+2019 right single quote (apostrophe)
    [InlineData("“Hi”", "\"Hi\"")] // U+201C/U+201D curly double quotes
    [InlineData("A–B", "A-B")]          // U+2013 en-dash
    [InlineData("A—B", "A-B")]          // U+2014 em-dash
    [InlineData("Wait…", "Wait...")]    // U+2026 ellipsis -> three dots
    // Punctuation-only: accented letters and non-Latin scripts are not this method's job.
    [InlineData("Beyoncé", "Beyoncé")]
    [InlineData("Москва", "Москва")]
    [InlineData("Plain Title", "Plain Title")]
    public void Transform_NormalizePunctuation_FoldsEachMapping_AndLeavesEverythingElse(
        string input, string expected)
    {
        Assert.Equal(expected, Sanitizer.NormalizePunctuation(input));
    }
}
