using System.Text.Json;
using System.Text.Json.Nodes;
using Renamer.Options;

namespace Renamer.Tests.Options;

/// <summary>
/// The name-to-id options conversion. Every case here is a way a user's configuration can be lost
/// silently, so each asserts what SURVIVES rather than only that the conversion ran.
/// </summary>
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

    [Theory]
    [InlineData("9", false)]
    [InlineData("0", false)]
    [InlineData("2147483647", false)]
    [InlineData("-9", false)]
    [InlineData("+9", false)]
    [InlineData("09", false)]
    [InlineData(" 9 ", false)]
    [InlineData("\t9\n", false)]
    [InlineData("1e3", true)]
    [InlineData("1.5", true)]
    [InlineData("9,9", true)]
    [InlineData("2147483648", true)]
    [InlineData("-2147483649", true)]
    [InlineData("\u00A09", true)]
    [InlineData("", true)]
    [InlineData("Anime", true)]
    public void DestinationKeySpelling_DecidesNameOrId(string key, bool isName)
    {
        // The settings panel refuses to save while this scan reports work, so its own `isIdKey` has to
        // read every spelling the way `int.TryParse` does here. This theory is the C# half of that
        // agreement, transcribed into the panel's `options.test.ts` as the same table.
        var root = new JsonObject
        {
            ["TagDestinations"] = new JsonObject { [key] = "D:/x" },
        };

        var scan = OptionsMigration.Scan(root.ToJsonString());

        Assert.Equal(isName, scan.Any);
        if (isName)
        {
            Assert.Equal([key], scan.Tags);
        }
        else
        {
            Assert.Empty(scan.Tags);
        }
    }
}

/// <summary>The conversion itself: what it rewrites, what it keeps, and what it reports losing.</summary>
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
        // it, ExcludeTags-as-names throws and the store answers a throw with defaults. Both halves run,
        // because a stored destination left as a bare string does not bind either.
        var named = OptionsMigration.Convert(LegacyBlob, Tags, Performers);
        var conversion = OptionsMigration.ConvertDestinationsToRoots(named.Json, ["/drama"]);

        var options = JsonSerializer.Deserialize<RenamerOptions>(
            conversion.Json, RenamerOptions.JsonOptions);

        Assert.NotNull(options);
        Assert.Equal([11], options!.Tags.WhitelistIds);
        Assert.Equal([12], options.Tags.BlacklistIds);
        Assert.Equal([21], options.Performers.WhitelistIds);
        Assert.Equal([13], options.ExcludeTagIds);
        Assert.Equal("/drama", options.TagDestinations[14].Root);
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

    [Theory]
    [InlineData("drama", 14)]
    [InlineData("DRAMA", 14)]
    [InlineData("dRaMa", 14)]
    [InlineData("drama ", null)]
    public void AStoredNameResolves_RegardlessOfLetterCase(string stored, int? expected)
    {
        // Matching a rule against a live entity name was OrdinalIgnoreCase before this conversion, so a
        // rule stored in a different case was LIVE and a case-sensitive lookup here drops it silently.
        // Trimming was never part of that matching, which is why the padded name resolves to nothing.
        var conversion = OptionsMigration.Convert(
            $$"""{ "ExcludeTags": ["{{stored}}"] }""", Tags, Performers);

        var options = JsonSerializer.Deserialize<RenamerOptions>(
            conversion.Json, RenamerOptions.JsonOptions)!;

        Assert.Equal(expected is null ? [] : (int[])[expected.Value], options.ExcludeTagIds);
    }

    [Fact]
    public void ARuleThatCoveredCaseVariantRows_ReportsTheNarrowing_AndPicksTheRowByData()
    {
        // Three rows sharing one name is a real library state: an entity's identity is its name paired
        // with a disambiguation, so distinct disambiguations coexist under one name. The rule covered all
        // three before the conversion and covers one after, so every file featuring the other two starts
        // behaving differently. The name RESOLVED, so the dropped-name trail says nothing about it, and
        // without this report the narrowing is invisible.
        //
        // "Dupe"/"dupe" is a case-variant pair no stored rule names. Reporting every such pair in a
        // library would bury the ones a rule actually narrows, so it stays out of the report.
        (int, string)[] rows = [(70, "Ada Vex"), (71, "ada vex"), (72, "Ada Vex"), (80, "Dupe"), (81, "dupe")];
        const string blob =
            """{ "Performers": { "Whitelist": ["Ada Vex", "ada vex"], "Blacklist": ["ADA VEX"] } }""";

        var conversion = OptionsMigration.Convert(blob, Tags, rows);

        // One report per stored spelling that named the entity, each naming the row the rule now covers
        // and the rows it no longer does. The pair "Dupe"/"dupe" is a case variant NO stored rule names,
        // and reporting every such pair in a library would bury the ones a rule actually narrows.
        Assert.Equal(
            ["ADA VEX", "Ada Vex", "ada vex"],
            conversion.CaseCollapses.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.All(
            conversion.CaseCollapses,
            c =>
            {
                Assert.Equal(70, c.MatchedId);
                Assert.Equal([71, 72], c.AlsoMatchedIds.Order());
            });
        Assert.Empty(conversion.DroppedNames);

        // Which row the rule now covers is decided by the DATA - the lowest id - and not by the order the
        // rows came back in, which is not something the user chose.
        (int, string)[] descending =
            [(81, "dupe"), (80, "Dupe"), (72, "Ada Vex"), (71, "ada vex"), (70, "Ada Vex")];

        Assert.All(
            OptionsMigration.Convert(blob, Tags, descending).CaseCollapses,
            c =>
            {
                Assert.Equal(70, c.MatchedId);
                Assert.Equal([71, 72], c.AlsoMatchedIds.Order());
            });
    }

    [Fact]
    public void EveryDroppedName_AcrossAllSixSites_IsReported()
    {
        // Every lookup table here is POPULATED and none of these names is in it, so each site is exercised
        // on the resolve-and-miss path rather than on the no-rows path. A name lost from any one site is
        // configuration the user does not get back.
        const string blob = """
            {
              "Performers": { "Whitelist": ["Nobody"], "Blacklist": ["Nobody Else"] },
              "Tags": { "Whitelist": ["ghost-a"], "Blacklist": ["ghost-b"] },
              "TagDestinations": { "ghost-c": "/media/x" },
              "ExcludeTags": ["ghost-d"]
            }
            """;

        var conversion = OptionsMigration.Convert(blob, Tags, Performers);

        Assert.Equal(
            ["Nobody", "Nobody Else", "ghost-a", "ghost-b", "ghost-c", "ghost-d"],
            conversion.DroppedNames.Order(StringComparer.Ordinal));

        var root = (JsonObject)JsonNode.Parse(conversion.Json)!;
        Assert.Empty(Ids(root["Tags"]!["WhitelistIds"]));
        Assert.Empty(Ids(root["Tags"]!["BlacklistIds"]));
        Assert.Empty(Ids(root["Performers"]!["WhitelistIds"]));
        Assert.Empty(Ids(root["Performers"]!["BlacklistIds"]));
        Assert.Empty(Ids(root["ExcludeTagIds"]));
        Assert.Empty(root["TagDestinations"]!.AsObject());
    }

    [Fact]
    public void AllSixMigratedSites_SerializeAsIdValued_SoReintroducingNameKeyingFailsHere()
    {
        // Goes red the moment a migrated site is name-keyed again: a change that reintroduces a
        // name-valued whitelist, exclude list or destination key fails on this shape rather than at run
        // time, as a rule that quietly stops matching. Both halves run first, because a stored destination
        // left as a bare string does not bind to the current model at all.
        var named = OptionsMigration.Convert(LegacyBlob, Tags, Performers);
        var placed = OptionsMigration.ConvertDestinationsToRoots(named.Json, ["/drama"]);
        var options = JsonSerializer.Deserialize<RenamerOptions>(placed.Json, RenamerOptions.JsonOptions)!;

        using var raw = JsonDocument.Parse(JsonSerializer.Serialize(options, RenamerOptions.JsonOptions));

        AssertIdArray(raw.RootElement.GetProperty("Tags"), "WhitelistIds");
        AssertIdArray(raw.RootElement.GetProperty("Tags"), "BlacklistIds");
        AssertIdArray(raw.RootElement.GetProperty("Performers"), "WhitelistIds");
        AssertIdArray(raw.RootElement.GetProperty("Performers"), "BlacklistIds");
        AssertIdArray(raw.RootElement, "ExcludeTagIds");

        foreach (var entry in raw.RootElement.GetProperty("TagDestinations").EnumerateObject())
        {
            Assert.True(
                int.TryParse(entry.Name, out _),
                $"TagDestinations key '{entry.Name}' is not an id - a name-keyed tag rule is back.");
        }

        static void AssertIdArray(JsonElement owner, string property)
        {
            var array = owner.GetProperty(property);
            Assert.NotEqual(0, array.GetArrayLength());
            foreach (var item in array.EnumerateArray())
            {
                Assert.True(
                    item.ValueKind == JsonValueKind.Number,
                    $"{property} holds {item.ValueKind} - a name-valued rule list is back.");
            }
        }
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
