using System.Text.Json;
using Renamer.Options;

namespace Renamer.Tests.Options;

public sealed class RenamerOptionsJsonTests
{

    [Fact]
    public void DurationFormat_Default_SerializesWithEscapedBackslashes_AndIsValidJson()
    {
        // The default DurationFormat is a TimeSpan format whose literal value contains backslashes
        // (hh\-mm\-ss). The serializer must escape each backslash so the stored blob is valid JSON a
        // strict reader (the settings panel) can parse back. A lone backslash here is what made the
        // panel fail with "Bad escaped character in JSON".
        var json = JsonSerializer.Serialize(new RenamerOptions(), RenamerOptions.JsonOptions);

        Assert.Contains(@"""DurationFormat"":""hh\\-mm\\-ss""", json); // escaped, not a lone backslash

        // It must also re-parse and round-trip the exact value.
        using var parsed = JsonDocument.Parse(json); // throws if the blob is not valid JSON
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);
        Assert.Equal(@"hh\-mm\-ss", reloaded!.DurationFormat);
    }




    [Fact]
    public void AutoRenamerOnUpdate_Defaults_Off()
    {
        Assert.False(new RenamerOptions().AutoRenamerOnUpdate); // opt-in, default OFF
    }




    [Fact]
    public void PersistedEnums_KeepTheMemberNameSpelling_NotTheWireSpelling()
    {
        // The enum types carry [JsonConverter(typeof(CamelCaseStringEnumConverter))] so the wire document
        // describes them as camelCase. RenamerOptions.JsonOptions declares a converter of its own at the
        // options level, which outranks a type attribute, so the persisted blob keeps the member name.
        // Asserted as literal text because a round-trip is symmetric: it agrees with itself whichever
        // spelling is written, and a flipped spelling would silently rewrite every saved setting.
        var options = new RenamerOptions
        {
            Case = CaseTransform.Title,
            Performers = new MultiValueOptions { OnOverflow = OverflowPolicy.KeepFirst, Sort = SortOrder.FavoriteFirst },
        };

        var json = JsonSerializer.Serialize(options, RenamerOptions.JsonOptions);

        Assert.Contains(@"""Case"":""Title""", json, StringComparison.Ordinal);
        Assert.Contains(@"""OnOverflow"":""KeepFirst""", json, StringComparison.Ordinal);
        Assert.Contains(@"""Sort"":""FavoriteFirst""", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SqueezeStudioNames_Defaults_Off()
    {
        Assert.False(new RenamerOptions().SqueezeStudioNames); // opt-in, default OFF
    }




    [Fact]
    public void GatingAndSuffix_Defaults_Match_ContextDecisions()
    {
        var o = new RenamerOptions();

        Assert.False(o.OnlyOrganized);                 // gate off by default
        Assert.Equal(new List<string> { "title" }, o.RequiredFields); // Title required by default
        Assert.Contains("{n}", o.DuplicateSuffixFormat); // counter placeholder present
    }

    // ---- field_replacer ----


    [Fact]
    public void FieldReplacers_Default_Empty()
    {
        Assert.Empty(new RenamerOptions().FieldReplacers); // default empty
    }


    // ---- prepositions_removal ----

    [Fact]
    public void StripLeadingArticles_And_Articles_Defaults()
    {
        var o = new RenamerOptions();
        Assert.False(o.StripLeadingArticles); // opt-in, default OFF
        Assert.Equal(new List<string> { "The", "A", "An" }, o.Articles); // default list
    }




    // ---- prevent_title_performer ----

    [Fact]
    public void PreventTitlePerformer_Defaults_Off()
    {
        Assert.False(new RenamerOptions().PreventTitlePerformer); // opt-in, default OFF
    }



    // ---- prevent_consecutive ----

    [Fact]
    public void PreventConsecutiveSegments_Defaults_On()
    {
        Assert.True(new RenamerOptions().PreventConsecutiveSegments); // on for a fresh install (cosmetic)
    }



    [Fact]
    public void NewFields_OmittedFromJson_LoadWithDefaults()
    {
        // forward-compat: a blob that predates these fields still loads, with the absent fields
        // taking their current defaults.
        const string json = """{"FilenameTemplate":"$title"}""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.FieldReplacers);
        Assert.False(loaded.StripLeadingArticles);
        Assert.Equal(new List<string> { "The", "A", "An" }, loaded.Articles);
        Assert.False(loaded.PreventTitlePerformer);     // opt-in, defaults off
        Assert.True(loaded.PreventConsecutiveSegments); // defaults on for a fresh install
    }

    // ---- the exclude system ----

    [Fact]
    public void ExcludeConfig_Defaults_Empty()
    {
        var o = new RenamerOptions();
        Assert.Empty(o.ExcludeTagIds);     // default empty = no excludes
        Assert.Empty(o.ExcludeStudioIds);  // default empty
        Assert.Empty(o.ExcludePaths);      // default empty
    }





    [Fact]
    public void ExcludeConfig_OmittedFromJson_LoadsWithDefaults()
    {
        // A blob predating the exclude fields still loads with empty excludes.
        const string json = """{"FilenameTemplate":"$title"}""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.ExcludeTagIds);
        Assert.Empty(loaded.ExcludeStudioIds);
        Assert.Empty(loaded.ExcludePaths);
    }

    // ---- NormalizePunctuation ----

    [Fact]
    public void NormalizePunctuation_Defaults_On()
    {
        Assert.True(new RenamerOptions().NormalizePunctuation); // on for a fresh install (folds smart quotes to ASCII)
    }



    [Fact]
    public void NormalizePunctuation_OmittedFromJson_LoadsTrue_ExplicitFalsePreserved()
    {
        // forward-compat: an old blob that predates the field loads with the true default; a blob that
        // explicitly stores false keeps that stored value (a present value is never overwritten).
        const string omitted = """{"FilenameTemplate":"$title"}""";
        var loadedOmitted = JsonSerializer.Deserialize<RenamerOptions>(omitted, RenamerOptions.JsonOptions);
        Assert.NotNull(loadedOmitted);
        Assert.True(loadedOmitted!.NormalizePunctuation);

        const string explicitFalse = """{"FilenameTemplate":"$title","NormalizePunctuation":false}""";
        var loadedFalse = JsonSerializer.Deserialize<RenamerOptions>(explicitFalse, RenamerOptions.JsonOptions);
        Assert.NotNull(loadedFalse);
        Assert.False(loadedFalse!.NormalizePunctuation);
    }

    // ---- removechar + filename-as-title ----

    [Fact]
    public void RemoveCharactersAndFilenameAsTitle_DefaultValues()
    {
        var o = new RenamerOptions();
        Assert.Equal(",#", o.RemoveCharacters); // default strips comma + hash out of the box
        Assert.True(o.FilenameAsTitle);         // basename fallback on for a fresh install
    }




    [Fact]
    public void RemoveCharactersAndFilenameAsTitle_OmittedFromJson_LoadWithDefaults()
    {
        const string json = """{"FilenameTemplate":"$title"}""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal(",#", loaded!.RemoveCharacters); // omitted → default (strips comma + hash)
        Assert.True(loaded.FilenameAsTitle); // defaults on for a fresh install
    }

    [Fact]
    public void StoredOldDefaultBlob_RoundTripsUnchanged_NotOverwrittenByNewDefaults()
    {
        // A blob saved before the default flip carries the old template + both flags off. Loading it
        // must return those stored values verbatim — the new defaults apply only to an absent field,
        // never to a present one, so an existing user's saved options never silently change.
        const string json =
            """{"FilenameTemplate":"$title{ [$resolution]}","PreventConsecutiveSegments":false,"FilenameAsTitle":false}""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal("$title{ [$resolution]}", loaded!.FilenameTemplate);
        Assert.False(loaded.PreventConsecutiveSegments);
        Assert.False(loaded.FilenameAsTitle);
    }

    [Fact]
    public void UnknownProperty_IsIgnored_OnLoad()
    {
        // forward-compat: a future field that this version does not know about.
        const string json =
            """{"FilenameTemplate":"$studio - $title","Case":"Title","UnknownFutureField":42}""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal("$studio - $title", loaded!.FilenameTemplate);
        Assert.Equal(CaseTransform.Title, loaded.Case);
    }

    [Fact]
    public void MissingProperty_Defaults_OnLoad()
    {
        // JSON that omits FilenameMax / FullPathMax — they must default.
        const string json = """{"FilenameTemplate":"$title"}""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal(255, loaded!.FilenameMax);
        Assert.Equal(259, loaded.FullPathMax);
        Assert.Equal(CaseTransform.None, loaded.Case);
    }


    [Fact]
    public void Defaults_Match_ContextDecisions()
    {
        var o = new RenamerOptions();

        Assert.Equal(255, o.FilenameMax);
        Assert.Equal(259, o.FullPathMax);
        Assert.Equal(CaseTransform.None, o.Case);
        Assert.False(o.AsciiTransliterate);

        Assert.Equal(" ", o.Performers.Separator);
        Assert.Equal(" ", o.Tags.Separator);
        Assert.Equal(0, o.Performers.MaxCount);
        Assert.Equal(OverflowPolicy.DropAll, o.Performers.OnOverflow);
        Assert.Equal(SortOrder.NameAsc, o.Performers.Sort);

        Assert.Equal(
            new List<string>
            {
                "videoCodec", "audioCodec", "frameRate", "resolution",
                "tags", "studioCode", "studio", "performers", "date",
            },
            o.DropOrder);
    }
}
