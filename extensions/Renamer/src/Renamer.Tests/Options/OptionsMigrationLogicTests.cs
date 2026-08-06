using System.Text.Json;
using Renamer.Options;

namespace Renamer.Tests.Options;

/// <summary>
/// The pure half of the one-time name→id options conversion: resolution, the case-collapse narrowing,
/// the dropped-name trail and the preservation of everything the converter does not model. No store, no
/// database context — every input here is hand-written, so nothing agrees with the code under test by
/// construction.
/// </summary>
[Trait("Tier", "L0")]
public sealed class OptionsMigrationLogicTests
{
    private static readonly (int Id, string Name)[] Tags =
    [
        (9, "Anime"),
        (15, "Sample"),
        (88, "4K"),
        (104, "Trailer"),
    ];

    private static readonly (int Id, string Name)[] Performers = [(311, "Jane Doe"), (312, "John Roe")];

    /// <summary>
    /// The blob a user who installed before the identity migration actually has on disk, transcribed by
    /// hand from the shape the shipped panel wrote — never generated from the code under test, which
    /// would only prove the converter agrees with itself.
    /// </summary>
    private const string LegacyBlob =
        """
        {
          "FilenameTemplate": "{$date - }$title",
          "FolderTemplate": "$studio",
          "AllowedRoots": ["/media"],
          "RequiredFields": ["title"],
          "Performers": { "Separator": " ", "Whitelist": ["jane doe"], "Blacklist": [] },
          "Tags": { "Separator": " ", "Whitelist": ["4k"], "Blacklist": ["Trailer"] },
          "StudioDestinations": { "42": "/media/studio-a" },
          "TagDestinations": { "Anime": "/media/anime" },
          "ExcludeTags": ["Sample", "4K"],
          "ExcludeStudioIds": [7],
          "AnUnmodelledKeyFromSomeFutureVersion": { "keep": "me" }
        }
        """;

    private static RenamerOptions Reload(string json) =>
        JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions)!;

    // ── Why the conversion cannot go through the typed model ──────────────────

    [Fact]
    public void TheLegacyBlob_DoesNotBindToTheCurrentModel_WhichIsWhyTheConverterReadsRawJson()
    {
        // A name-keyed TagDestinations is not a Dictionary<int, string>. The options store answers this
        // throw by returning DEFAULTS for the WHOLE object, so a converter that loaded through it would
        // convert defaults and then persist them over every setting the user has — not just the six
        // migrated fields. This test is the reason OptionsMigration works on the raw JSON.
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<RenamerOptions>(LegacyBlob, RenamerOptions.JsonOptions));
    }

    [Fact]
    public void ALegacyBlob_Converts_WithoutLosingAnyFieldTheConverterDoesNotModel()
    {
        var converted = OptionsMigration.Convert(LegacyBlob, Tags, Performers);
        var options = Reload(converted.Json);

        // The six migrated fields, resolved.
        Assert.Equal([88], options.Tags.WhitelistIds);
        Assert.Equal([104], options.Tags.BlacklistIds);
        Assert.Equal([311], options.Performers.WhitelistIds);
        Assert.Empty(options.Performers.BlacklistIds);
        Assert.Equal([15, 88], options.ExcludeTagIds);
        Assert.Equal(new Dictionary<int, string> { [9] = "/media/anime" }, options.TagDestinations);

        // Everything else, untouched — including the fields a failed load would have reset to defaults.
        Assert.Equal("{$date - }$title", options.FilenameTemplate);
        Assert.Equal("$studio", options.FolderTemplate);
        Assert.Equal(["/media"], options.AllowedRoots);
        Assert.Equal(["title"], options.RequiredFields);
        Assert.Equal(" ", options.Tags.Separator);
        Assert.Equal(new Dictionary<int, string> { [42] = "/media/studio-a" }, options.StudioDestinations);
        Assert.Equal([7], options.ExcludeStudioIds);

        // A key the converter does not model at all still survives the rewrite.
        using var raw = JsonDocument.Parse(converted.Json);
        Assert.Equal(
            "me",
            raw.RootElement.GetProperty("AnUnmodelledKeyFromSomeFutureVersion").GetProperty("keep").GetString());

        // No legacy spelling is left behind for a later reader to half-honour.
        Assert.False(raw.RootElement.TryGetProperty("ExcludeTags", out _));
        Assert.False(raw.RootElement.GetProperty("Tags").TryGetProperty("Whitelist", out _));
        Assert.False(raw.RootElement.GetProperty("Performers").TryGetProperty("Whitelist", out _));
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("4K", 88)]
    [InlineData("4k", 88)]
    [InlineData("4k ", null)]   // trimming was never part of matching; only letter case was
    [InlineData("aNiMe", 9)]
    public void AStoredNameResolves_RegardlessOfLetterCase(string stored, int? expected)
    {
        // Matching a rule against a live entity name was OrdinalIgnoreCase before this migration, so a
        // rule stored in a different case was LIVE and a case-sensitive lookup would silently drop it.
        var converted = OptionsMigration.Convert(
            $$"""{ "ExcludeTags": ["{{stored}}"] }""", Tags, Performers);

        Assert.Equal(expected is null ? [] : (int[])[expected.Value], Reload(converted.Json).ExcludeTagIds);
    }

    [Fact]
    public void TwoStoredNamesDifferingOnlyByCase_CollapseToOneId_AndTheCountIsPinned()
    {
        // The one genuine narrowing this migration carries: "Sample" and "4K"/"4k" were three stored
        // entries matching two tags; afterwards they are two ids. Pinning the COUNT records the
        // narrowing rather than leaving it to be discovered.
        var converted = OptionsMigration.Convert(
            """{ "ExcludeTags": ["Sample", "4K", "4k"] }""", Tags, Performers);

        var ids = Reload(converted.Json).ExcludeTagIds;
        Assert.Equal(3 - 1, ids.Count);
        Assert.Equal([15, 88], ids);
        Assert.Empty(converted.DroppedNames);
    }

    [Fact]
    public void WhereTwoTagsDifferOnlyByCase_TheCollapseIsDecidedByTheData_NotByRowOrder()
    {
        (int, string)[] ascending = [(70, "4K"), (71, "4k")];
        (int, string)[] descending = [(71, "4k"), (70, "4K")];
        const string blob = """{ "ExcludeTags": ["4K"] }""";

        Assert.Equal(
            Reload(OptionsMigration.Convert(blob, ascending, Performers).Json).ExcludeTagIds,
            Reload(OptionsMigration.Convert(blob, descending, Performers).Json).ExcludeTagIds);
        Assert.Equal([70], Reload(OptionsMigration.Convert(blob, descending, Performers).Json).ExcludeTagIds);
    }

    [Fact]
    public void AStoredNameMatchingNothing_IsDropped_AndReported()
    {
        // The same value the host-absent styles spec seeds for its stale-chip fixture, so the two agree.
        var converted = OptionsMigration.Convert(
            """{ "ExcludeTags": ["tag-that-no-longer-exists", "Sample"] }""", Tags, Performers);

        Assert.Equal([15], Reload(converted.Json).ExcludeTagIds);
        Assert.Equal(["tag-that-no-longer-exists"], converted.DroppedNames);
    }

    [Fact]
    public void EveryDroppedName_AcrossAllSixFields_IsReported()
    {
        const string blob =
            """
            {
              "Performers": { "Whitelist": ["Nobody"], "Blacklist": ["Nobody Else"] },
              "Tags": { "Whitelist": ["ghost-a"], "Blacklist": ["ghost-b"] },
              "TagDestinations": { "ghost-c": "/media/x" },
              "ExcludeTags": ["ghost-d"]
            }
            """;

        var converted = OptionsMigration.Convert(blob, Tags, Performers);

        Assert.Equal(
            ["ghost-a", "ghost-b", "Nobody", "Nobody Else", "ghost-d", "ghost-c"],
            converted.DroppedNames);
        var options = Reload(converted.Json);
        Assert.Empty(options.Tags.WhitelistIds);
        Assert.Empty(options.Tags.BlacklistIds);
        Assert.Empty(options.Performers.WhitelistIds);
        Assert.Empty(options.Performers.BlacklistIds);
        Assert.Empty(options.ExcludeTagIds);
        Assert.Empty(options.TagDestinations);
    }

    [Fact]
    public void NoConvertedFieldHoldsAnIdThatIsNotInTheSuppliedEntityList()
    {
        var converted = OptionsMigration.Convert(LegacyBlob, Tags, Performers);
        var options = Reload(converted.Json);

        var tagIds = Tags.Select(t => t.Id).ToHashSet();
        var performerIds = Performers.Select(p => p.Id).ToHashSet();

        Assert.All(options.ExcludeTagIds, id => Assert.Contains(id, tagIds));
        Assert.All(options.TagDestinations.Keys, id => Assert.Contains(id, tagIds));
        Assert.All(options.Tags.WhitelistIds, id => Assert.Contains(id, tagIds));
        Assert.All(options.Tags.BlacklistIds, id => Assert.Contains(id, tagIds));
        Assert.All(options.Performers.WhitelistIds, id => Assert.Contains(id, performerIds));
        Assert.All(options.Performers.BlacklistIds, id => Assert.Contains(id, performerIds));
    }

    /// <summary>
    /// Goes RED the moment any of the six migrated fields is name-keyed again. A future change that
    /// reintroduces a name-valued whitelist, exclude list or destination key fails HERE, on a
    /// serialize/deserialize round-trip of a converted blob, rather than at run time as a rule that
    /// quietly stops matching.
    /// </summary>
    [Fact]
    public void EverySixthMigratedField_SurvivesARoundTripAsIdValued_ReintroducingNameKeyingFailsHere()
    {
        var converted = Reload(OptionsMigration.Convert(LegacyBlob, Tags, Performers).Json);
        var json = JsonSerializer.Serialize(converted, RenamerOptions.JsonOptions);
        var reloaded = Reload(json);

        Assert.Equal(converted, reloaded);

        using var raw = JsonDocument.Parse(json);
        AssertIdArray(raw.RootElement.GetProperty("Tags"), "WhitelistIds");
        AssertIdArray(raw.RootElement.GetProperty("Tags"), "BlacklistIds");
        AssertIdArray(raw.RootElement.GetProperty("Performers"), "WhitelistIds");
        AssertIdArray(raw.RootElement.GetProperty("Performers"), "BlacklistIds");
        AssertIdArray(raw.RootElement, "ExcludeTagIds");

        foreach (var entry in raw.RootElement.GetProperty("TagDestinations").EnumerateObject())
        {
            Assert.True(
                int.TryParse(entry.Name, out _),
                $"TagDestinations key '{entry.Name}' is not an id — a name-keyed tag rule is back.");
        }

        static void AssertIdArray(JsonElement owner, string property)
        {
            foreach (var item in owner.GetProperty(property).EnumerateArray())
            {
                Assert.True(
                    item.ValueKind == JsonValueKind.Number,
                    $"{property} holds {item.ValueKind} — a name-valued rule list is back.");
            }
        }
    }

    // ── Destination keys: the one ambiguous field ─────────────────────────────

    [Fact]
    public void TagDestinationsAlreadyKeyedByIds_AreLeftAlone()
    {
        // What a FRESH install writes before this conversion has ever run. Treating those keys as names
        // would resolve them against nothing and delete the user's routing on the next host start.
        var converted = OptionsMigration.Convert(
            """{ "TagDestinations": { "9": "/media/anime", "88": "/media/4k" } }""", Tags, Performers);

        Assert.Equal(
            new Dictionary<int, string> { [9] = "/media/anime", [88] = "/media/4k" },
            Reload(converted.Json).TagDestinations);
        Assert.Empty(converted.DroppedNames);
    }

    [Fact]
    public void TwoDestinationNamesResolvingToOneId_KeepTheFirstInDocumentOrder()
    {
        // Also the crash case: the panel's string-keyed map editor treats "4K" and "4k" as two rules, so
        // this blob is reachable. Reading it through a case-INSENSITIVE JsonNode parse throws
        // ArgumentException the first time the map is enumerated, which would abort the whole
        // conversion and leave the settings permanently unreadable.
        var converted = OptionsMigration.Convert(
            """{ "TagDestinations": { "4K": "/media/first", "4k": "/media/second" } }""", Tags, Performers);

        Assert.Equal(
            new Dictionary<int, string> { [88] = "/media/first" },
            Reload(converted.Json).TagDestinations);
    }

    // ── Scan: what the initialize seam asks before it touches a database ──────

    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData("", 0, 0)]
    [InlineData("not json at all", 0, 0)]
    [InlineData("""{ "FilenameTemplate": "$title" }""", 0, 0)]
    [InlineData("""{ "TagDestinations": { "9": "/media/anime" } }""", 0, 0)]
    [InlineData("""{ "Tags": { "WhitelistIds": [88] } }""", 0, 0)]
    [InlineData("""{ "excludetags": ["4k"] }""", 1, 0)]
    [InlineData("""{ "TagDestinations": { "Anime": "/media/anime" } }""", 1, 0)]
    [InlineData("""{ "Tags": { "Whitelist": ["4k"], "Blacklist": ["Trailer"] } }""", 2, 0)]
    [InlineData("""{ "Performers": { "Blacklist": ["Jane Doe"] } }""", 0, 1)]
    // The shape every pre-migration install actually stored: each legacy key present, none of them
    // holding a name. Counting the KEYS here instead would demand a performer table this user may
    // legitimately not have, and the conversion would defer on every start forever.
    [InlineData(
        """
        {
          "Performers": { "Whitelist": [], "Blacklist": [] },
          "Tags": { "Whitelist": [], "Blacklist": [] },
          "ExcludeTags": []
        }
        """, 0, 0)]
    [InlineData(
        """
        {
          "Performers": { "Whitelist": [], "Blacklist": [] },
          "Tags": { "Whitelist": [], "Blacklist": [] },
          "ExcludeTags": [],
          "TagDestinations": { "Anime": "/media/anime" }
        }
        """, 1, 0)]
    public void Scan_CountsTheNamesEachHalfStillNeedsResolved(string? json, int tags, int performers)
    {
        var legacy = OptionsMigration.Scan(json);

        Assert.Equal(tags, legacy.Tags);
        Assert.Equal(performers, legacy.Performers);
        Assert.Equal(tags + performers > 0, legacy.Any);
    }

    [Fact]
    public void AConvertedBlob_NeedsNoFurtherConversion()
    {
        var converted = OptionsMigration.Convert(LegacyBlob, Tags, Performers);

        Assert.False(OptionsMigration.Scan(converted.Json).Any);
    }
}
