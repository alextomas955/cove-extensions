using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Tests;

public class SanitizerTests
{
    // ---- CleanSegment: illegal / control chars ----

    [Fact]
    public void CleanSegment_IllegalChar_RemovedByDefault()
    {
        // Default IllegalReplacement = "" => stripped.
        var o = new RenamerOptions();
        Assert.Equal("ab", Sanitizer.CleanSegment("a:b", o));
    }

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

    [Fact]
    public void CleanSegment_IllegalChar_ReplacedWhenReplacementSet()
    {
        var o = new RenamerOptions { IllegalReplacement = "_" };
        Assert.Equal("a_b", Sanitizer.CleanSegment("a:b", o));
    }

    [Fact]
    public void CleanSegment_ControlChars_AlwaysStripped()
    {
        var o = new RenamerOptions { IllegalReplacement = "_" };
        // Control chars are dropped, not replaced (they are not in the illegal set).
        Assert.Equal("ab", Sanitizer.CleanSegment("a\tb", o));
    }

    // ---- CleanSegment: space replacement ----

    [Fact]
    public void CleanSegment_Spaces_KeptByDefault()
    {
        var o = new RenamerOptions();
        Assert.Equal("a b", Sanitizer.CleanSegment("a b", o));
    }

    [Fact]
    public void CleanSegment_Spaces_ReplacedWhenSet()
    {
        var o = new RenamerOptions { SpaceReplacement = "_" };
        Assert.Equal("a_b", Sanitizer.CleanSegment("a b", o));
    }

    // ---- CleanSegment: collapse + trim ----

    [Fact]
    public void CleanSegment_CollapsesRepeatedSpaces()
    {
        var o = new RenamerOptions();
        Assert.Equal("a b", Sanitizer.CleanSegment("a    b", o));
    }

    [Fact]
    public void CleanSegment_CollapsesRepeatedSpaceReplacement()
    {
        var o = new RenamerOptions { SpaceReplacement = "_" };
        Assert.Equal("a_b", Sanitizer.CleanSegment("a   b", o));
    }

    [Fact]
    public void CleanSegment_TrimsLeadingTrailingSpacesAndDots()
    {
        var o = new RenamerOptions();
        Assert.Equal("title", Sanitizer.CleanSegment("  .title. ", o));
    }

    [Fact]
    public void CleanSegment_EmptyAfterCleaning_ReturnsEmpty()
    {
        var o = new RenamerOptions();
        Assert.Equal("", Sanitizer.CleanSegment("  ...  ", o));
    }

    // ---- CleanSegment: reserved device names ----

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
        // The trailing dot is trimmed first, leaving "CON", which the guard then disambiguates.
        var o = new RenamerOptions();
        Assert.Equal("CON_", Sanitizer.CleanSegment("CON.", o));
    }

    [Fact]
    public void CleanSegment_IllegalStripProducesReservedStem_GetsDisambiguatingUnderscore()
    {
        // ':' is stripped by default, leaving "CON", which the guard then disambiguates.
        var o = new RenamerOptions();
        Assert.Equal("CON_", Sanitizer.CleanSegment("CON:", o));
    }

    // ---- CleanSegment: character removal ----

    [Fact]
    public void CleanSegment_RemoveSet_DropsConfiguredChars()
    {
        var o = new RenamerOptions { RemoveCharacters = "," };
        Assert.Equal("ab", Sanitizer.CleanSegment("a,b", o));
    }

    [Fact]
    public void CleanSegment_RemoveSet_DropsEveryListedChar()
    {
        var o = new RenamerOptions { RemoveCharacters = ",#" };
        Assert.Equal("abc", Sanitizer.CleanSegment("a,b#c", o));
    }

    [Theory]
    [InlineData("plain title")]
    [InlineData("a:b")]               // illegal strip still applies under an empty remove-set
    [InlineData("a    b")]            // space collapse still applies
    [InlineData("  .title. ")]        // edge trim still applies
    public void CleanSegment_RemoveSet_Empty_IsByteIdenticalNoOp(string input)
    {
        // An empty RemoveCharacters must leave the output identical to the pre-change pipeline,
        // both for a plain input and for ones that exercise illegal/space/trim handling.
        var withEmpty = new RenamerOptions { RemoveCharacters = "" };
        var withoutField = new RenamerOptions();
        Assert.Equal(Sanitizer.CleanSegment(input, withoutField), Sanitizer.CleanSegment(input, withEmpty));
    }

    [Fact]
    public void CleanSegment_RemoveSet_DropsBeforeIllegalReplacement()
    {
        // ':' is both OS-illegal and in the remove-set; it is removed, NOT turned into "_".
        var o = new RenamerOptions { RemoveCharacters = ":", IllegalReplacement = "_" };
        Assert.Equal("ab", Sanitizer.CleanSegment("a:b", o));
    }

    [Fact]
    public void CleanSegment_RemoveSet_ComposesWithSpaceReplacement()
    {
        var o = new RenamerOptions { RemoveCharacters = ",", SpaceReplacement = "_" };
        Assert.Equal("a_b_c", Sanitizer.CleanSegment("a, b c", o));
    }

    // ---- ApplyCase ----

    [Fact]
    public void Transform_CaseNone_LeavesUnchanged()
    {
        Assert.Equal("Hello World", Sanitizer.ApplyCase("Hello World", CaseTransform.None));
    }

    [Fact]
    public void Transform_CaseLower_Lowercases()
    {
        Assert.Equal("hello world", Sanitizer.ApplyCase("Hello World", CaseTransform.Lower));
    }

    [Fact]
    public void Transform_TitleCase_TitleCasesEachWord()
    {
        Assert.Equal("Hello World", Sanitizer.ApplyCase("hello world", CaseTransform.Title));
    }

    // ---- Transliterate ----

    [Fact]
    public void Transform_Transliterate_FoldsDiacritics()
    {
        Assert.Equal("Beyonce", Sanitizer.Transliterate("Beyoncé")); // Beyoncé -> Beyonce
        Assert.Equal("Espana", Sanitizer.Transliterate("España"));   // España -> Espana
    }

    [Fact]
    public void Transform_Transliterate_LeavesNonLatinNonEmpty()
    {
        // Cyrillic must NOT be emptied — transliteration folds diacritics only, not whole scripts.
        var cyr = "Москва"; // "Москва"
        var result = Sanitizer.Transliterate(cyr);
        Assert.False(string.IsNullOrEmpty(result));
        Assert.Equal(cyr, result); // no diacritics to fold -> unchanged, still non-empty
    }

    [Fact]
    public void Transform_Transliterate_PlainAsciiUnchanged()
    {
        Assert.Equal("Plain Title", Sanitizer.Transliterate("Plain Title"));
    }

    // ---- NormalizePunctuation ----

    [Theory]
    [InlineData("It‘s", "It's")]        // U+2018 left single quote
    [InlineData("It’s", "It's")]        // U+2019 right single quote (apostrophe)
    [InlineData("“Hi”", "\"Hi\"")] // U+201C/U+201D curly double quotes
    [InlineData("A–B", "A-B")]          // U+2013 en-dash
    [InlineData("A—B", "A-B")]          // U+2014 em-dash
    [InlineData("Wait…", "Wait...")]    // U+2026 ellipsis -> three dots
    public void Transform_NormalizePunctuation_FoldsEachMapping(string input, string expected)
    {
        Assert.Equal(expected, Sanitizer.NormalizePunctuation(input));
    }

    [Fact]
    public void Transform_NormalizePunctuation_LeavesLettersAndNonLatinUnchanged()
    {
        // Punctuation-only: accented letters and non-Latin scripts are NOT this method's job.
        Assert.Equal("Beyoncé", Sanitizer.NormalizePunctuation("Beyoncé"));
        Assert.Equal("Москва", Sanitizer.NormalizePunctuation("Москва"));
    }

    [Fact]
    public void Transform_NormalizePunctuation_PlainAsciiUnchanged()
    {
        Assert.Equal("Plain Title", Sanitizer.NormalizePunctuation("Plain Title"));
    }
}
