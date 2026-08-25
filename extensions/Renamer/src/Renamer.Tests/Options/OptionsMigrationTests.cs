using System.Text.Json;
using System.Text.Json.Nodes;
using Renamer.Options;

namespace Renamer.Tests.Options;

/// <summary>
/// The name-to-id options conversion. Every case here is a way a user's configuration can be lost
/// silently, so each asserts what SURVIVES rather than only that the conversion ran.
/// </summary>
[Trait("Tier", "L0")]
public sealed class OptionsMigrationScanTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void UnusableBlob_NeedsNothing(string? json)
    {
        var scan = OptionsMigration.Scan(json);

        Assert.False(scan.Any);
        Assert.Empty(scan.Tags);
        Assert.Empty(scan.Performers);
    }

    [Fact]
    public void EmptyLegacyCollections_NeedNothing()
    {
        // The pre-conversion panel serialized its whole defaults object, so a user who configured no
        // rule at all still stored these keys empty. Treating a present-but-empty key as work would
        // defer the conversion forever on such an install.
        const string json = """
            {
              "Tags": { "Whitelist": [], "Blacklist": [] },
              "Performers": { "Whitelist": [], "Blacklist": [] },
              "ExcludeTags": [],
              "TagDestinations": {}
            }
            """;

        Assert.False(OptionsMigration.Scan(json).Any);
    }

    [Fact]
    public void EveryLegacySite_ContributesItsNames()
    {
        const string json = """
            {
              "Tags": { "Whitelist": ["keepTag"], "Blacklist": ["dropTag"] },
              "Performers": { "Whitelist": ["keepP"], "Blacklist": ["dropP"] },
              "ExcludeTags": ["excludedTag"],
              "TagDestinations": { "routedTag": "/dest" }
            }
            """;

        var scan = OptionsMigration.Scan(json);

        Assert.Equal(["dropTag", "excludedTag", "keepTag", "routedTag"], scan.Tags.Order(StringComparer.Ordinal));
        Assert.Equal(["dropP", "keepP"], scan.Performers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RepeatedName_IsAskedForOnce()
    {
        const string json = """
            {
              "Tags": { "Whitelist": ["anime"], "Blacklist": ["ANIME"] },
              "ExcludeTags": ["Anime"],
              "TagDestinations": { "aNiMe": "/dest" }
            }
            """;

        Assert.Single(OptionsMigration.Scan(json).Tags);
    }

    [Fact]
    public void AlreadyConvertedBlob_NeedsNothing()
    {
        const string json = """
            {
              "Tags": { "WhitelistIds": [1], "BlacklistIds": [2] },
              "Performers": { "WhitelistIds": [3] },
              "ExcludeTagIds": [4],
              "TagDestinations": { "5": "/dest" }
            }
            """;

        Assert.False(OptionsMigration.Scan(json).Any);
    }

    [Fact]
    public void LegacyKeyInAnotherCasing_IsStillFound()
    {
        // RenamerOptions.JsonOptions binds case-insensitively, so a blob spelled this way is one the
        // model accepts. Missing it here would stamp it as converted and leave its rules unbindable.
        const string json = """
            {
              "tags": { "whitelist": ["anime"] },
              "excludeTags": ["raw"],
              "tagDestinations": { "drama": "/d" }
            }
            """;

        var scan = OptionsMigration.Scan(json);

        Assert.Equal(["anime", "drama", "raw"], scan.Tags.Order(StringComparer.Ordinal));
    }
}

/// <summary>The conversion itself: what it rewrites, what it keeps, and what it reports losing.</summary>
[Trait("Tier", "L0")]
public sealed class OptionsMigrationConvertTests
{
    private const string LegacyBlob = """
        {
          "FilenameTemplate": "$title",
          "Tags": { "Separator": "_", "Whitelist": ["anime"], "Blacklist": ["raw"] },
          "Performers": { "Separator": " & ", "Whitelist": ["Ann"], "Blacklist": ["Bob"] },
          "ExcludeTags": ["spoiler"],
          "TagDestinations": { "drama": "/drama" }
        }
        """;

    private static readonly (int Id, string Name)[] Tags =
        [(11, "anime"), (12, "raw"), (13, "spoiler"), (14, "drama")];

    private static readonly (int Id, string Name)[] Performers = [(21, "Ann"), (22, "Bob")];

    private static JsonObject Converted(
        string json,
        IReadOnlyList<(int Id, string Name)>? tags = null,
        IReadOnlyList<(int Id, string Name)>? performers = null)
        => (JsonObject)JsonNode.Parse(
            OptionsMigration.Convert(json, tags ?? Tags, performers ?? Performers).Json)!;

    [Fact]
    public void EveryLegacySite_BecomesIds_AndTheLegacyKeyIsGone()
    {
        var root = Converted(LegacyBlob);

        Assert.Equal([11], Ids(root["Tags"]!["WhitelistIds"]));
        Assert.Equal([12], Ids(root["Tags"]!["BlacklistIds"]));
        Assert.Equal([21], Ids(root["Performers"]!["WhitelistIds"]));
        Assert.Equal([22], Ids(root["Performers"]!["BlacklistIds"]));
        Assert.Equal([13], Ids(root["ExcludeTagIds"]));
        Assert.Equal("/drama", (string?)root["TagDestinations"]!["14"]);

        // A legacy key left beside its replacement is a state the model cannot express, so a later
        // read would have to pick one and the two would drift.
        Assert.Null(root["Tags"]!.AsObject().FirstOrDefault(e => e.Key == "Whitelist").Value);
        Assert.Null(root["Tags"]!.AsObject().FirstOrDefault(e => e.Key == "Blacklist").Value);
        Assert.False(root.ContainsKey("ExcludeTags"));
        Assert.False(root["TagDestinations"]!.AsObject().ContainsKey("drama"));
    }

    [Fact]
    public void UnmodelledKeys_SurviveVerbatim()
    {
        // A hand-edited or newer-version key must not be a casualty of a conversion that does not
        // understand it.
        var root = Converted("""
            {
              "FilenameTemplate": "$title",
              "SomethingThisConverterNeverHeardOf": { "deep": [1, 2] },
              "ExcludeTags": ["spoiler"]
            }
            """);

        Assert.Equal("$title", (string?)root["FilenameTemplate"]);
        Assert.Equal("[1,2]", root["SomethingThisConverterNeverHeardOf"]!["deep"]!.ToJsonString());
    }

    [Fact]
    public void SiblingOptionsInsideAConvertedGroup_AreUntouched()
    {
        var root = Converted(LegacyBlob);

        Assert.Equal("_", (string?)root["Tags"]!["Separator"]);
        Assert.Equal(" & ", (string?)root["Performers"]!["Separator"]);
    }

    [Fact]
    public void UnresolvableName_IsDroppedAndReported()
    {
        var conversion = OptionsMigration.Convert(
            """{ "ExcludeTags": ["gone", "spoiler"] }""", Tags, Performers);

        var root = (JsonObject)JsonNode.Parse(conversion.Json)!;
        Assert.Equal([13], Ids(root["ExcludeTagIds"]));
        Assert.Equal("gone", Assert.Single(conversion.DroppedNames));
    }

    [Fact]
    public void UnresolvableDestinationKey_IsDroppedAndReported()
    {
        var conversion = OptionsMigration.Convert(
            """{ "TagDestinations": { "gone": "/g", "drama": "/drama" } }""", Tags, Performers);

        var root = (JsonObject)JsonNode.Parse(conversion.Json)!;
        Assert.Equal("/drama", (string?)root["TagDestinations"]!["14"]);
        Assert.Single(root["TagDestinations"]!.AsObject());
        Assert.Equal("gone", Assert.Single(conversion.DroppedNames));
    }

    [Fact]
    public void CaseVariantsOfOneName_CollapseToTheLowestId_AndAreReported()
    {
        // A rule for "anime" used to apply to every case variant. It can only carry one id now, and
        // the lowest is chosen so two installs holding the same rows never disagree.
        (int, string)[] rows = [(31, "Anime"), (12, "anime"), (44, "ANIME")];

        var conversion = OptionsMigration.Convert("""{ "ExcludeTags": ["anime"] }""", rows, []);

        var root = (JsonObject)JsonNode.Parse(conversion.Json)!;
        Assert.Equal([12], Ids(root["ExcludeTagIds"]));

        var collapse = Assert.Single(conversion.CaseCollapses);
        Assert.Equal("anime", collapse.Name);
        Assert.Equal(12, collapse.MatchedId);
        Assert.Equal([31, 44], collapse.AlsoMatchedIds.Order());
    }

    [Fact]
    public void SingleMatch_ReportsNoCollapse()
    {
        var conversion = OptionsMigration.Convert("""{ "ExcludeTags": ["spoiler"] }""", Tags, Performers);

        Assert.Empty(conversion.CaseCollapses);
    }

    [Fact]
    public void TwoDestinationKeysResolvingToOneId_KeepTheFirstAndReportTheOther()
    {
        // The resolver takes the first matching rule, so stored order decides the survivor: reversing
        // it here would hand the user a different destination than the pre-conversion resolver chose.
        (int, string)[] rows = [(14, "drama"), (14, "DRAMA")];

        var conversion = OptionsMigration.Convert(
            """{ "TagDestinations": { "drama": "/first", "DRAMA": "/second" } }""", rows, []);

        var root = (JsonObject)JsonNode.Parse(conversion.Json)!;
        Assert.Equal("/first", (string?)root["TagDestinations"]!["14"]);

        var discarded = Assert.Single(conversion.DiscardedDestinations);
        Assert.Equal("DRAMA", discarded.Key);
        Assert.Equal(14, discarded.Id);
        Assert.Equal("drama", discarded.ClaimedBy);
    }

    [Fact]
    public void TwoNameListEntriesResolvingToOneId_AreNotDuplicated()
    {
        (int, string)[] rows = [(13, "spoiler"), (13, "SPOILER")];

        var conversion = OptionsMigration.Convert(
            """{ "ExcludeTags": ["spoiler", "SPOILER"] }""", rows, []);

        var root = (JsonObject)JsonNode.Parse(conversion.Json)!;
        Assert.Equal([13], Ids(root["ExcludeTagIds"]));
    }

    [Fact]
    public void LegacyKeyInAnotherCasing_IsConverted_AndItsOriginalSpellingRemoved()
    {
        var root = Converted("""{ "tags": { "whitelist": ["anime"] }, "excludeTags": ["spoiler"] }""");

        Assert.Equal([11], Ids(root["tags"]!["WhitelistIds"]));
        Assert.False(root["tags"]!.AsObject().ContainsKey("whitelist"));
        Assert.Equal([13], Ids(root["ExcludeTagIds"]));
        Assert.False(root.ContainsKey("excludeTags"));
    }

    [Fact]
    public void ConvertedBlob_BindsToTheCurrentModel()
    {
        // The point of the whole conversion: the result is a blob the options store can read. Before
        // it, ExcludeTags-as-names throws and the store answers a throw with defaults.
        var conversion = OptionsMigration.Convert(LegacyBlob, Tags, Performers);

        var options = JsonSerializer.Deserialize<RenamerOptions>(
            conversion.Json, RenamerOptions.JsonOptions);

        Assert.NotNull(options);
        Assert.Equal([11], options!.Tags.WhitelistIds);
        Assert.Equal([12], options.Tags.BlacklistIds);
        Assert.Equal([21], options.Performers.WhitelistIds);
        Assert.Equal([13], options.ExcludeTagIds);
        Assert.Equal("/drama", options.TagDestinations[14]);
        Assert.Equal("$title", options.FilenameTemplate);
    }

    [Fact]
    public void ConvertingTwice_IsAStableNoOp()
    {
        var once = OptionsMigration.Convert(LegacyBlob, Tags, Performers).Json;
        var twice = OptionsMigration.Convert(once, Tags, Performers);

        Assert.False(OptionsMigration.Scan(once).Any);
        Assert.Empty(twice.DroppedNames);
        Assert.Equal(
            JsonNode.Parse(once)!.ToJsonString(),
            JsonNode.Parse(twice.Json)!.ToJsonString());
    }

    [Fact]
    public void NoRowsAtAll_DropsEveryNameAndReportsEachOne()
    {
        // This is what the seam's zero-row deferral exists to prevent reaching: with nothing to
        // resolve against, the conversion is a total loss of the user's entity rules.
        var conversion = OptionsMigration.Convert(LegacyBlob, [], []);

        Assert.Equal(["Ann", "Bob", "anime", "drama", "raw", "spoiler"], conversion.DroppedNames.Order(StringComparer.Ordinal));
    }

    private static int[] Ids(JsonNode? node) => [.. node!.AsArray().Select(n => (int)n!)];
}
