using System.Text.Json;
using Renamer.Options;
using Renamer.Tests.TestSupport;

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

    // A changed default changes behavior for every install that never set the member.
    [Fact]
    public void Defaults_AreTheShippedValues()
    {
        var o = new RenamerOptions();

        Assert.Equal("{$date - }$title{ [$resolution]}", o.FilenameTemplate);
        Assert.Equal("", o.FolderTemplate);
        Assert.Equal(255, o.FilenameMax);
        Assert.Equal(259, o.FullPathMax);
        Assert.Equal(CaseTransform.None, o.Case);
        Assert.False(o.AsciiTransliterate);
        Assert.False(o.AutoRenamerOnUpdate);
        Assert.False(o.OnlyOrganized);
        Assert.Equal(["title"], o.RequiredFields);
        Assert.Contains("{n}", o.DuplicateSuffixFormat);
        Assert.False(o.SqueezeStudioNames);
        Assert.False(o.StripLeadingArticles);
        Assert.Equal(["The", "A", "An"], o.Articles);
        Assert.False(o.PreventTitlePerformer);
        Assert.True(o.PreventConsecutiveSegments);
        Assert.True(o.NormalizePunctuation);
        Assert.Equal(",#", o.RemoveCharacters);
        Assert.True(o.FilenameAsTitle);
        Assert.Empty(o.FieldReplacers);
        Assert.Empty(o.ExcludeTagIds);
        Assert.Empty(o.ExcludeStudioIds);
        Assert.Empty(o.ExcludePaths);

        Assert.Equal(" ", o.Performers.Separator);
        Assert.Equal(" ", o.Tags.Separator);
        Assert.Equal(0, o.Performers.MaxCount);
        Assert.Equal(OverflowPolicy.DropAll, o.Performers.OnOverflow);
        Assert.Equal(SortOrder.NameAsc, o.Performers.Sort);
        Assert.Equal(
            ["videoCodec", "audioCodec", "frameRate", "resolution", "tags", "studioCode", "studio", "performers", "date"],
            o.DropOrder);
    }

    // A blob saved before a member existed carries no value for it.
    [Fact]
    public void AnEmptyBlob_LoadsEveryDefault()
    {
        var loaded = JsonSerializer.Deserialize<RenamerOptions>("{}", RenamerOptions.JsonOptions);

        Assert.Equal(OptionsJson.Canonical(new RenamerOptions()), OptionsJson.Canonical(loaded));
    }

    [Fact]
    public void StoredOldDefaultBlob_RoundTripsUnchanged_NotOverwrittenByNewDefaults()
    {
        // A blob saved before the default flip carries the old template + both flags off. Loading it
        // must return those stored values verbatim - the new defaults apply only to an absent field,
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
}
