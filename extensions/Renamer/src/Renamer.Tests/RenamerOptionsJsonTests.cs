using System.Text.Json;
using Renamer.Options;

namespace Renamer.Tests;

public sealed class RenamerOptionsJsonTests
{
    [Fact]
    public void DefaultOptions_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions();

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(original, reloaded); // record value equality (deep)
    }

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
    public void PathDestinationValue_WithWindowsPath_RoundTripsAsValidJson()
    {
        // A routing destination holding a Windows path has backslashes that likewise must be escaped
        // so the stored blob stays valid JSON across a save → load round-trip.
        var original = new RenamerOptions { DefaultDestination = @"G:\Media\Sorted" };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        using var parsed = JsonDocument.Parse(json); // valid JSON, no lone backslash
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(@"G:\Media\Sorted", reloaded!.DefaultDestination);
    }

    [Fact]
    public void CustomizedOptions_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions
        {
            FilenameTemplate = "$studio - $title [$resolution]",
            FolderTemplate = "$studio/$year",
            Case = CaseTransform.Title,
            AsciiTransliterate = true,
            DropOrder = ["title", "studio", "tags"],
            Performers = new MultiValueOptions
            {
                Separator = " & ",
                MaxCount = 3,
                OnOverflow = OverflowPolicy.KeepFirst,
                Sort = SortOrder.None,
                Whitelist = ["Alice", "Bob"],
                Blacklist = ["Carol"],
            },
            Tags = new MultiValueOptions { Separator = "_" },
        };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(original, reloaded);
    }

    [Fact]
    public void GatingAndSuffixFields_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions
        {
            OnlyOrganized = true,
            RequiredFields = ["title", "studio"],
            DuplicateSuffixFormat = " ({n})",
            AutoRenamerOnUpdate = true,
        };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        // A JSON round-trip allocates a fresh RequiredFields list; structural Equals must hold.
        Assert.Equal(original, reloaded);
        Assert.True(reloaded!.AutoRenamerOnUpdate); // the new flag survives the round-trip
    }

    [Fact]
    public void AutoRenamerOnUpdate_Defaults_Off()
    {
        Assert.False(new RenamerOptions().AutoRenamerOnUpdate); // opt-in, default OFF
    }

    [Fact]
    public void PerformerGenderAndSortOptions_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions
        {
            Performers = new MultiValueOptions
            {
                Separator = ", ",
                MaxCount = 2,
                OnOverflow = OverflowPolicy.KeepFirst,
                Sort = SortOrder.FavoriteFirst,
                IgnoreGenders = ["Male"],
                GenderOrder = ["Female", "Male"],
            },
        };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        // A round-trip allocates fresh gender lists and re-parses the new SortOrder string; the
        // structural Equals/GetHashCode wiring must keep them value-equal.
        Assert.Equal(original, reloaded);
        Assert.Equal(SortOrder.FavoriteFirst, reloaded!.Performers.Sort);
    }

    [Fact]
    public void PerformerGenderOptions_Participate_In_Equality()
    {
        // A difference in a gender field alone must make two options instances UNEQUAL — proves the
        // new fields are wired into the hand-written MultiValueOptions Equals/GetHashCode.
        var a = new RenamerOptions { Performers = new MultiValueOptions { IgnoreGenders = ["Male"] } };
        var b = new RenamerOptions { Performers = new MultiValueOptions { IgnoreGenders = ["Female"] } };
        Assert.NotEqual(a, b);

        var c = new RenamerOptions { Performers = new MultiValueOptions { GenderOrder = ["Male"] } };
        var d = new RenamerOptions { Performers = new MultiValueOptions { GenderOrder = ["Female"] } };
        Assert.NotEqual(c, d);
    }

    [Fact]
    public void NewSortOrderValues_RoundTrip_AsStrings()
    {
        // The enum is serialized as a stable string (JsonStringEnumConverter); the new members must
        // survive a round-trip by name.
        foreach (var sort in new[] { SortOrder.IdAsc, SortOrder.FavoriteFirst })
        {
            var original = new RenamerOptions { Performers = new MultiValueOptions { Sort = sort } };
            var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
            var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);
            Assert.Equal(sort, reloaded!.Performers.Sort);
        }
    }

    [Fact]
    public void SqueezeStudioNames_Defaults_Off()
    {
        Assert.False(new RenamerOptions().SqueezeStudioNames); // opt-in, default OFF
    }

    [Fact]
    public void SqueezeStudioNames_Participates_In_Equality()
    {
        // A bare flag difference must make two options instances UNEQUAL — proves the flag
        // is wired into the hand-written Equals/GetHashCode (not silently ignored).
        var off = new RenamerOptions { SqueezeStudioNames = false };
        var on = new RenamerOptions { SqueezeStudioNames = true };
        Assert.NotEqual(off, on);
    }

    [Fact]
    public void SqueezeStudioNames_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions { SqueezeStudioNames = true };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(original, reloaded);
        Assert.True(reloaded!.SqueezeStudioNames); // the new flag survives the round-trip
    }

    [Fact]
    public void AutoRenamerOnUpdate_Participates_In_Equality()
    {
        // A bare flag difference must make two options instances UNEQUAL — proves the flag
        // is wired into the hand-written Equals/GetHashCode (not silently ignored).
        var off = new RenamerOptions { AutoRenamerOnUpdate = false };
        var on = new RenamerOptions { AutoRenamerOnUpdate = true };
        Assert.NotEqual(off, on);
    }

    [Fact]
    public void GatingAndSuffix_Defaults_Match_ContextDecisions()
    {
        var o = new RenamerOptions();

        Assert.False(o.OnlyOrganized);                 // gate off by default
        Assert.Equal(new List<string> { "title" }, o.RequiredFields); // Title required by default
        Assert.Contains("{n}", o.DuplicateSuffixFormat); // counter placeholder present
    }

    // ---- FIELD-02: field_replacer ----

    [Fact]
    public void FieldReplacers_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions
        {
            FieldReplacers =
            [
                new FieldReplaceRule { TargetToken = "studio", Find = "'", Replace = "" },
                new FieldReplaceRule { TargetToken = "title", Find = ":", Replace = " -" },
            ],
        };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        // Proves the FieldReplaceRule record AND the List are wired into structural equality
        // (a fresh list of fresh records must still compare value-equal).
        Assert.Equal(original, reloaded);
        Assert.Equal(2, reloaded!.FieldReplacers.Count);
    }

    [Fact]
    public void FieldReplacers_Default_Empty()
    {
        Assert.Empty(new RenamerOptions().FieldReplacers); // default empty
    }

    [Fact]
    public void FieldReplacers_Participates_In_Equality()
    {
        var none = new RenamerOptions();
        var withRule = new RenamerOptions
        {
            FieldReplacers = [new FieldReplaceRule { TargetToken = "studio", Find = "'", Replace = "" }],
        };
        Assert.NotEqual(none, withRule);
    }

    // ---- FIELD-03: prepositions_removal ----

    [Fact]
    public void StripLeadingArticles_And_Articles_Defaults()
    {
        var o = new RenamerOptions();
        Assert.False(o.StripLeadingArticles); // opt-in, default OFF
        Assert.Equal(new List<string> { "The", "A", "An" }, o.Articles); // default list
    }

    [Fact]
    public void Articles_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions
        {
            StripLeadingArticles = true,
            Articles = ["The", "A", "An", "Le", "La"],
        };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(original, reloaded);
        Assert.True(reloaded!.StripLeadingArticles);
    }

    [Fact]
    public void StripLeadingArticles_Participates_In_Equality()
    {
        var off = new RenamerOptions { StripLeadingArticles = false };
        var on = new RenamerOptions { StripLeadingArticles = true };
        Assert.NotEqual(off, on);
    }

    [Fact]
    public void Articles_Participates_In_Equality()
    {
        var def = new RenamerOptions();
        var custom = new RenamerOptions { Articles = ["The"] };
        Assert.NotEqual(def, custom);
    }

    // ---- prevent_title_performer ----

    [Fact]
    public void PreventTitlePerformer_Defaults_Off()
    {
        Assert.False(new RenamerOptions().PreventTitlePerformer); // opt-in, default OFF
    }

    [Fact]
    public void PreventTitlePerformer_Participates_In_Equality()
    {
        var off = new RenamerOptions { PreventTitlePerformer = false };
        var on = new RenamerOptions { PreventTitlePerformer = true };
        Assert.NotEqual(off, on);
    }

    [Fact]
    public void PreventTitlePerformer_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions { PreventTitlePerformer = true };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(original, reloaded);
        Assert.True(reloaded!.PreventTitlePerformer); // the new flag survives the round-trip
    }

    // ---- FIELD-06: prevent_consecutive ----

    [Fact]
    public void PreventConsecutiveSegments_Defaults_On()
    {
        Assert.True(new RenamerOptions().PreventConsecutiveSegments); // on for a fresh install (cosmetic)
    }

    [Fact]
    public void PreventConsecutiveSegments_Participates_In_Equality()
    {
        var off = new RenamerOptions { PreventConsecutiveSegments = false };
        var on = new RenamerOptions { PreventConsecutiveSegments = true };
        Assert.NotEqual(off, on);
    }

    [Fact]
    public void PreventConsecutiveSegments_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions { PreventConsecutiveSegments = true };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(original, reloaded);
        Assert.True(reloaded!.PreventConsecutiveSegments); // the new flag survives the round-trip
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

    // ---- EXCL-01/02/03: exclude system ----

    [Fact]
    public void ExcludeConfig_Defaults_Empty()
    {
        var o = new RenamerOptions();
        Assert.Empty(o.ExcludeTags);       // EXCL-01 default empty = no excludes (legacy behavior)
        Assert.Empty(o.ExcludeStudioIds);  // EXCL-02 default empty
        Assert.Empty(o.ExcludePaths);      // EXCL-03 default empty
    }

    [Fact]
    public void ExcludeConfig_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions
        {
            ExcludeTags = ["anime", "raw"],
            ExcludeStudioIds = [42, 7],
            ExcludePaths =
            [
                new ExcludeRule { Pattern = "media/protected", IsRegex = false },
                new ExcludeRule { Pattern = @"^media/keep/\d+$", IsRegex = true },
            ],
        };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        // Proves the ExcludeRule record AND the three collections are wired into structural equality
        // (fresh lists of fresh records must still compare value-equal).
        Assert.Equal(original, reloaded);
        Assert.Equal(2, reloaded!.ExcludeTags.Count);
        Assert.Equal(2, reloaded.ExcludeStudioIds.Count);
        Assert.Equal(2, reloaded.ExcludePaths.Count);
    }

    [Fact]
    public void ExcludeTags_Participates_In_Equality()
    {
        var none = new RenamerOptions();
        var withTag = new RenamerOptions { ExcludeTags = ["anime"] };
        Assert.NotEqual(none, withTag);
    }

    [Fact]
    public void ExcludeStudioIds_Participates_In_Equality()
    {
        var none = new RenamerOptions();
        var withStudio = new RenamerOptions { ExcludeStudioIds = [42] };
        Assert.NotEqual(none, withStudio);
    }

    [Fact]
    public void ExcludePaths_Participates_In_Equality()
    {
        var none = new RenamerOptions();
        var withPath = new RenamerOptions
        {
            ExcludePaths = [new ExcludeRule { Pattern = "media/protected", IsRegex = false }],
        };
        Assert.NotEqual(none, withPath);
    }

    [Fact]
    public void ExcludeConfig_OmittedFromJson_LoadsWithDefaults()
    {
        // forward-compat: a blob predating the EXCL-* fields still loads with empty excludes.
        const string json = """{"FilenameTemplate":"$title"}""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.ExcludeTags);
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
    public void NormalizePunctuation_Participates_In_Equality()
    {
        // A bare flag difference must make two options instances UNEQUAL — proves the flag
        // is wired into the hand-written Equals/GetHashCode (not silently ignored).
        var off = new RenamerOptions { NormalizePunctuation = false };
        var on = new RenamerOptions { NormalizePunctuation = true };
        Assert.NotEqual(off, on);
    }

    [Fact]
    public void NormalizePunctuation_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions { NormalizePunctuation = false };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(original, reloaded);
        Assert.False(reloaded!.NormalizePunctuation); // an explicit false survives the round-trip
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
    public void RemoveCharacters_Participates_In_Equality()
    {
        var none = new RenamerOptions { RemoveCharacters = "" };
        var withSet = new RenamerOptions { RemoveCharacters = ",#" };
        Assert.NotEqual(none, withSet);
    }

    [Fact]
    public void FilenameAsTitle_Participates_In_Equality()
    {
        var off = new RenamerOptions { FilenameAsTitle = false };
        var on = new RenamerOptions { FilenameAsTitle = true };
        Assert.NotEqual(off, on);
    }

    [Fact]
    public void RemoveCharactersAndFilenameAsTitle_RoundTrip_AreEqual()
    {
        var original = new RenamerOptions { RemoveCharacters = ",#", FilenameAsTitle = true };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(original, reloaded);
        Assert.Equal(",#", reloaded!.RemoveCharacters);
        Assert.True(reloaded.FilenameAsTitle);
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
        // A blob saved before the default flip carries the OLD template + both flags off. Loading it
        // must return those stored values verbatim — the new defaults apply only to an ABSENT field,
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
    public void Enum_Serializes_AsStableString_NotInteger()
    {
        var opts = new RenamerOptions { Case = CaseTransform.Title };

        var json = JsonSerializer.Serialize(opts, RenamerOptions.JsonOptions);

        Assert.Contains("\"Title\"", json);
        Assert.DoesNotContain("\"Case\":2", json); // not the numeric ordinal
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
