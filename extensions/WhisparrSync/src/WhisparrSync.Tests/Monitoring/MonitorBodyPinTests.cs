using System.Globalization;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// The external facts this product's monitoring rests on, pinned against documents the two
// measured builds produced. The captured documents are inputs. Every expected value below was
// written out by hand, because one computed from the document it checks would agree with that
// document whatever it said.
// Nothing here reads a status. One generation answers an identifier it does not know with a
// success and an empty list, and answers a body whose fields it dropped with a created status,
// so a status is not evidence. Every assertion is on the parsed shape or on the content of a
// document.
// Whisparr v2 publishes no contract, so every shape it answers with is a hand transcription that
// survives only here.
// A pin that goes red reports that the fact changed. Re-measure it against the new image and
// re-decide the code; never edit the fixture to match. Every fixture file names the build it came
// from, so a stale one is visible.
public sealed class MonitorBodyPinTests
{
    private const string V3Build = "3.3.8.1097";

    private const string V3StudioFixture = "whisparr-v3-3.3.8.1097-studio-resource.json";
    private const string V3StudioAfterEditorFixture = "whisparr-v3-3.3.8.1097-studio-after-editor.json";
    private const string V3StudioReadDateGateSetFixture =
        "whisparr-v3-3.3.8.1097-studio-read-after-date-set.json";
    private const string V3StudioReadDateGateAbsentFixture =
        "whisparr-v3-3.3.8.1097-studio-read-after-date-absent.json";
    private const string V3MinimalRefusalFixture = "whisparr-v3-3.3.8.1097-studio-minimal-refusal.json";
    private const string V3MediaManagementFixture = "whisparr-v3-3.3.8.1097-media-management.json";
    private const string V3SchemasFixture = "whisparr-v3-3.3.8.1097-resource-schemas.json";
    private const string V3ImportModesFixture = "whisparr-v3-3.3.8.1097-import-modes.json";
    private const string V3CommandsFixture = "whisparr-v3-3.3.8.1097-command-payloads.json";
    private const string V3SceneAddAcceptedFixture = "whisparr-v3-3.3.8.1097-scene-add-accepted.json";
    private const string V3SceneAddAlreadyHeldFixture = "whisparr-v3-3.3.8.1097-scene-add-already-held.json";
    private const string V3SceneAddUnknownFixture = "whisparr-v3-3.3.8.1097-scene-add-unknown-identifier.json";
    private const string V3SceneAddTitlelessFixture = "whisparr-v3-3.3.8.1097-scene-add-titleless-refusal.json";
    private const string V3SceneAddTitleReplacedFixture = "whisparr-v3-3.3.8.1097-scene-add-title-replaced.json";

    private const string V2AddRefusalFixture = "whisparr-v2-2.2.0.231-series-add-refusal.json";
    private const string V2SeriesFixture = "whisparr-v2-2.2.0.231-series-resource.json";
    private const string V2SeriesAfterEditorFixture = "whisparr-v2-2.2.0.231-series-after-editor.json";
    private const string V2MediaManagementFixture = "whisparr-v2-2.2.0.231-media-management.json";
    private const string V2SeasonPassRefusalFixture = "whisparr-v2-2.2.0.231-seasonpass-no-body-refusal.json";
    private const string V2QueueFixture = "whisparr-v2-2.2.0.231-queue.json";
    private const string V2MonitorOptionsFixture = "whisparr-v2-2.2.0.231-monitor-options.json";
    private const string V2ImportModesFixture = "whisparr-v2-2.2.0.231-import-modes.json";
    private const string V2CommandsFixture = "whisparr-v2-2.2.0.231-command-payloads.json";

    // The identifier the entity that was added is named by on v3.
    private const string StudioForeignId = "44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e";

    // The identifier the scene that was registered twice is named by.
    private const string RegisteredSceneForeignId = "023bacff-8d1d-4f27-bac5-bdaf833f5616";

    // The identifier the registration control used, which no provider lists.
    private const string UnknownSceneForeignId = "00000000-0000-4000-8000-000000000000";

    // The number the metadata source names that site by.
    private const int SiteEntityId = 3372;

    private static readonly AddDefaults Defaults = new(4, "/config/library");

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    // Measured on 3.3.8.1097: an add missing either NOT NULL column answers a raw database
    // message and a stack trace. The add path has no validation rule set in front of it, so the
    // database constraint is the validator and the answer is unreadable. Nothing reads it.
    [Fact]
    public void V3RefusesAnAddMissingEitherColumnItsDatabaseRequires()
    {
        var refusal = Object(V3MinimalRefusalFixture);

        Assert.Contains(
            "NOT NULL constraint failed: Studios.RootFolderPath",
            refusal["message"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Contains(
            "SQLiteException",
            refusal["description"]!.GetValue<string>(),
            StringComparison.Ordinal);

        var composed = ComposedBody.Of(V3BodyProjector.AddStudio(
            StudioForeignId, MonitorScope.AllScenes, Defaults, Now));
        Assert.True(composed.ContainsKey("rootFolderPath"), $"the add omits a column {V3Build} requires");
        Assert.True(composed.ContainsKey("tags"), $"the add omits a column {V3Build} requires");
    }

    // Measured on 3.3.8.1097: the scene resource has a validation rule set the studio resource
    // lacks, and it fails on emptiness rather than absence. A whitespace title is refused in the
    // same words.
    [Fact]
    public void V3RefusesASceneRegistrationCarryingNoTitle()
    {
        var refusal = Assert.IsType<JsonObject>(Array(V3SceneAddTitlelessFixture).Single());

        Assert.Equal("Title", refusal["propertyName"]!.GetValue<string>());
        Assert.Equal("NotEmptyValidator", refusal["errorCode"]!.GetValue<string>());
        Assert.Equal("'Title' must not be empty.", refusal["errorMessage"]!.GetValue<string>());

        var composed = ComposedBody.Of(V3BodyProjector.AddScene(RegisteredSceneForeignId, Defaults));
        Assert.False(
            string.IsNullOrWhiteSpace(composed["title"]?.GetValue<string>()),
            $"the scene registration omits the member {V3Build} refuses an empty one on");
    }

    // Measured on 3.3.8.1097: a second registration of one scene and a registration of a
    // well-formed identifier no provider lists answer the same status and the same content type.
    // The error code member is what tells them apart, so a run classifies on it.
    [Fact]
    public void V3NamesASceneItAlreadyHoldsByAnErrorCodeTheControlDoesNotCarry()
    {
        var alreadyHeld = Assert.IsType<JsonObject>(Array(V3SceneAddAlreadyHeldFixture).Single());
        var unknown = Assert.IsType<JsonObject>(Array(V3SceneAddUnknownFixture).Single());

        Assert.Equal("ForeignId", alreadyHeld["propertyName"]!.GetValue<string>());
        Assert.Equal("MovieExistsValidator", alreadyHeld["errorCode"]!.GetValue<string>());
        Assert.Equal(
            "This item has already been added", alreadyHeld["errorMessage"]!.GetValue<string>());
        Assert.Equal(
            RegisteredSceneForeignId, alreadyHeld["attemptedValue"]!.GetValue<string>());

        Assert.Equal("StashDB", unknown["propertyName"]!.GetValue<string>());
        Assert.Equal(
            "A movie with this ID was not found. Path: ", unknown["errorMessage"]!.GetValue<string>());
        Assert.Equal(UnknownSceneForeignId, unknown["attemptedValue"]!.GetValue<string>());
        Assert.False(
            unknown.ContainsKey("errorCode"),
            $"{V3Build} names the control by an error code, so the two are no longer told apart on it");
    }

    // Measured on 3.3.8.1097: the instance replaces the title a registration carried with its own
    // resolution of the identifier, along with the folder it chose and the studio it attributed.
    [Fact]
    public void V3ReplacesTheTitleASceneRegistrationCarried()
    {
        var registered = Object(V3SceneAddTitleReplacedFixture);
        const string sentAsTitle = "027393c9-e589-4548-8a7f-c04292a9de14";

        Assert.Equal(sentAsTitle, registered["foreignId"]!.GetValue<string>());
        Assert.Equal("Nadia Noel", registered["title"]!.GetValue<string>());
        Assert.NotEqual(sentAsTitle, registered["title"]!.GetValue<string>());
        Assert.Equal("1000 Facials", registered["studioTitle"]!.GetValue<string>());
        Assert.DoesNotContain(
            sentAsTitle, registered["title"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // Measured on 3.3.8.1097: the echo drops the top-level suppression member and keeps the
    // add-options one, so a composed body carries both spellings.
    [Fact]
    public void ASceneRegistrationV3AcceptedEchoesTheSuppressionFlagSet()
    {
        var registered = Object(V3SceneAddAcceptedFixture);
        var echoed = Assert.IsType<JsonObject>(registered["addOptions"]);

        Assert.Equal(RegisteredSceneForeignId, registered["foreignId"]!.GetValue<string>());
        Assert.Equal("scene", registered["itemType"]!.GetValue<string>());
        Assert.True(registered["monitored"]!.GetValue<bool>());
        Assert.False(echoed["searchForMovie"]!.GetValue<bool>());
        Assert.Equal("sceneOnly", echoed["monitor"]!.GetValue<string>());
        Assert.Equal("manual", echoed["addMethod"]!.GetValue<string>());
        Assert.False(
            registered.ContainsKey("searchOnAdd"),
            $"{V3Build} now echoes the top-level flag, so the echo is evidence about it");
    }

    // Measured on 3.3.8.1097: the date gate is sent as an instant and held as a date, so a caller
    // comparing the two has to compare dates.
    [Fact]
    public void TheDateGateIsHeldInADifferentSpellingFromTheOneItIsSentIn()
    {
        var sent = ComposedBody
            .Of(V3BodyProjector.AddStudio(StudioForeignId, MonitorScope.FutureScenes, Defaults, Now))
            ["afterDate"]!
            .GetValue<string>();
        var held = Object(V3StudioFixture)["afterDate"]!.GetValue<string>();

        Assert.Equal("2026-09-02T00:00:00Z", sent);
        Assert.Equal("2026-09-02", held);
        Assert.NotEqual(sent, held);
        Assert.Equal(
            DateTimeOffset.Parse(sent, CultureInfo.InvariantCulture).Date,
            DateTime.Parse(held, CultureInfo.InvariantCulture).Date);
    }

    // Measured on 3.3.8.1097 from two reads of one studio, added once with the date gate and once
    // without it. Present-and-null was not what was observed, which is what licenses reading an
    // absent member as the wider scope rather than as an unknown.
    // The gate's value is transcribed here and read nowhere else.
    [Fact]
    public void V3StudioReadCarriesTheDateGateOnlyWhenOneWasSet()
    {
        var set = Object(V3StudioReadDateGateSetFixture);
        var absent = Object(V3StudioReadDateGateAbsentFixture);

        Assert.True(set.ContainsKey("afterDate"));
        Assert.Equal("2026-09-03", set["afterDate"]!.GetValue<string>());

        Assert.False(absent.ContainsKey("afterDate"));
        Assert.Null(absent["afterDate"]);

        // One entity, one difference: the member the two scopes are told apart by.
        Assert.Equal(StudioForeignId, set["foreignId"]!.GetValue<string>());
        Assert.Equal(StudioForeignId, absent["foreignId"]!.GetValue<string>());
        Assert.Equal(
            set.Select(member => member.Key).Where(key => key != "afterDate").Order(),
            absent.Select(member => member.Key).Order());
    }

    // Measured on 3.3.8.1097: an add carrying a zero quality profile is accepted and echoed back.
    // An entity stored under it monitors and then never acquires, and nothing in the answer says
    // so, which is why the stop is this product's own.
    [Fact]
    public void V3AcceptsAProfileItCanNeverActOnAndThisProductDoesNot()
    {
        Assert.Equal(0, Object(V3StudioFixture)["qualityProfileId"]!.GetValue<int>());
        Assert.True(Object(V3StudioFixture)["monitored"]!.GetValue<bool>());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ComposedBody.Of(V3BodyProjector.AddStudio(
                StudioForeignId, MonitorScope.AllScenes, new AddDefaults(0, "/config/library"), Now)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V2BodyProjector.AddStudio(
                SiteEntityId, MonitorScope.AllScenes, new AddDefaults(0, "/config/library")));
    }

    // Measured on 3.3.8.1097: a just-added entity reports a catalogue of zero before anything has
    // read one, so a count taken then would be a confident zero this product cannot support.
    [Fact]
    public void AFreshlyAddedEntityReportsNoCatalogueAtAll()
    {
        var studio = Object(V3StudioFixture);

        Assert.Equal(0, studio["sceneCount"]!.GetValue<int>());
        Assert.Equal(0, studio["totalSceneCount"]!.GetValue<int>());
    }

    // Measured on 3.3.8.1097 from the entity read before and after a flip carrying the id array
    // and the flag: the flag is the only difference.
    [Fact]
    public void V3EditorLeavesEveryFieldTheRequestDoesNotName()
    {
        AssertOnlyTheFlagChanged(Object(V3StudioFixture), Object(V3StudioAfterEditorFixture));

        Assert.Equal(
            ["monitored", "studioIds"],
            ComposedBody.Of(V3BodyProjector.SetStudioMonitored(1, monitored: false))
                .Select(member => member.Key)
                .Order());
    }

    // Measured against the contract 3.3.8.1097 publishes: the date gate is declared on one
    // resource and on no other, the editor resource included, so a scope change is the one case
    // the editor route cannot express.
    [Fact]
    public void TheDateGateIsDeclaredOnOneResourceAndOnNoOther()
    {
        var schemas = Object(V3SchemasFixture);

        Assert.Equal(162, schemas["schemaCount"]!.GetValue<int>());
        Assert.True(Declares(schemas, "StudioResource", "afterDate"));
        Assert.False(Declares(schemas, "PerformerResource", "afterDate"));
        Assert.False(Declares(schemas, "StudioEditorResource", "afterDate"));
        Assert.True(Declares(schemas, "PerformerResource", "monitored"));
    }

    // Transcribed from the interface bundle each of 3.3.8.1097 and 2.2.0.231 ships. The two arrays
    // are compared with each other and with a hand-written expectation, because comparing them
    // only with each other would pass if both changed the same way.
    [Fact]
    public void BothGenerationsOfferTheSameThreeImportModesAndNoInPlaceOne()
    {
        var expected = new[] { "chooseImportMode", "move", "copy" };
        var v3 = Keys(V3ImportModesFixture, "key");
        var v2 = Keys(V2ImportModesFixture, "key");

        Assert.Equal(expected, v3);
        Assert.Equal(expected, v2);
        Assert.Equal(v3, v2);

        // The first is a placeholder the interface renders unselectable, so two are reachable.
        Assert.True(Array(V3ImportModesFixture)[0]!["disabled"]!.GetValue<bool>());
        Assert.Equal("HardlinkCopyFiles", Array(V3ImportModesFixture)[2]!["label"]!.GetValue<string>());
    }

    // Measured on 3.3.8.1097 and 2.2.0.231 from the configuration each answered with. It is a
    // default rather than a guarantee, so it is read before acting rather than assumed.
    [Fact]
    public void BothGenerationsLinkAFileIntoPlaceByDefault()
    {
        Assert.True(Object(V3MediaManagementFixture)["copyUsingHardlinks"]!.GetValue<bool>());
        Assert.True(Object(V2MediaManagementFixture)["copyUsingHardlinks"]!.GetValue<bool>());
    }

    // Measured on 2.2.0.231. Whisparr v3 accepts both, so the product's guarantee rests on neither
    // generation: the profile is stopped before it is sent, and the root is read from the instance
    // rather than chosen.
    [Fact]
    public void V2RefusesAProfileAndARootV3Accepts()
    {
        var refused = Array(V2AddRefusalFixture)
            .Select(entry => (JsonObject)entry!)
            .ToDictionary(
                entry => entry["propertyName"]!.GetValue<string>(),
                entry => entry["errorCode"]!.GetValue<string>(),
                StringComparer.Ordinal);

        Assert.Equal("GreaterThanValidator", refused["QualityProfileId"]);
        Assert.Equal("RootFolderExistsValidator", refused["RootFolderPath"]);
    }

    // Measured on 2.2.0.231 from the entity read before and after a flip carrying the id array and
    // the flag. The per-year flags are what a wider body would silently overwrite, and the user
    // owns every one of them.
    [Fact]
    public void V2EditorLeavesEveryPerYearFlagAsItWas()
    {
        AssertOnlyTheFlagChanged(Object(V2SeriesFixture), Object(V2SeriesAfterEditorFixture));

        Assert.Equal(
            ["monitored", "seriesIds"],
            ComposedV2Body.Of(V2BodyProjector.SetMonitored(1, monitored: false))
                .Select(member => member.Key)
                .Order());
    }

    // Measured on 2.2.0.231: the catalogue is divided into years. No wording a user reads may
    // carry that field's name, so nothing composed on this path spells it.
    [Fact]
    public void V2CatalogueIsDividedIntoYears()
    {
        var divisions = Array(Object(V2SeriesFixture), "seasons")
            .Select(entry => ((JsonObject)entry!)["seasonNumber"]!.GetValue<int>())
            .ToArray();

        Assert.NotEmpty(divisions);
        Assert.All(divisions, division => Assert.InRange(division, 2000, 2100));
    }

    // Measured on 2.2.0.231: the scope route refuses a request with no body, so naming the verb is
    // not enough to probe it and the body is what makes it work.
    [Fact]
    public void TheScopeRouteRefusesARequestWithNoBody()
    {
        var refusal = Object(V2SeasonPassRefusalFixture);

        Assert.Contains(
            "A non-empty request body is required.",
            refusal["errors"]!.ToJsonString(),
            StringComparison.Ordinal);

        // The composed body names the entity inside an array of objects, which is the shape that route
        // reads. A scalar there is accepted by nothing.
        Assert.IsType<JsonArray>(
            ComposedV2Body.Of(V2BodyProjector.SetScope(1, MonitorScope.AllScenes))["series"]);
    }

    // Measured on 2.2.0.231 from the queue as it read after an add, a flag flip and two scope
    // changes. The composed-body assertions carry the never-acquire guarantee; this is the
    // instance agreeing with them once.
    [Fact]
    public void TheMeasuredSequencePutNothingInTheAcquisitionQueue()
    {
        var queue = Object(V2QueueFixture);

        Assert.Equal(0, queue["totalRecords"]!.GetValue<int>());
        Assert.Empty(Assert.IsType<JsonArray>(queue["records"]));
    }

    // Transcribed from the interface bundle each of 3.3.8.1097 and 2.2.0.231 ships. All the shapes
    // either generation uses are named, because the split is per generation and not per command.
    [Fact]
    public void TheCommandPayloadsSplitBetweenAnArrayAndAScalar()
    {
        var v3 = Array(V3CommandsFixture)
            .ToDictionary(
                entry => ((JsonObject)entry!)["name"]!.GetValue<string>(),
                entry => (JsonObject)entry!,
                StringComparer.Ordinal);
        var v2 = Array(V2CommandsFixture)
            .ToDictionary(
                entry => ((JsonObject)entry!)["name"]!.GetValue<string>(),
                entry => (JsonObject)entry!,
                StringComparer.Ordinal);

        Assert.Equal(["PerformersSearch", "RefreshStudios", "StudiosSearch"], v3.Keys.Order());
        Assert.Equal(["RefreshSeries", "SeriesSearch"], v2.Keys.Order());

        Assert.IsType<JsonArray>(v3["StudiosSearch"]["studioIds"]);
        Assert.IsType<JsonArray>(v3["RefreshStudios"]["studioIds"]);
        Assert.IsType<JsonArray>(v3["PerformersSearch"]["performerIds"]);

        Assert.Null(v2["SeriesSearch"]["seriesId"] as JsonArray);
        Assert.Equal(1, v2["SeriesSearch"]["seriesId"]!.GetValue<int>());
        Assert.Null(v2["RefreshSeries"]["seriesId"] as JsonArray);
        Assert.Equal(1, v2["RefreshSeries"]["seriesId"]!.GetValue<int>());
    }

    // Transcribed from the interface bundle and the localization file 2.2.0.231 ships. The two
    // this product composes are the two whose words are clean, and they are that generation's own
    // words.
    [Fact]
    public void ElevenMonitorOptionsAreOfferedAndThisProductComposesTwoOfThem()
    {
        var options = Array(V2MonitorOptionsFixture).Select(entry => (JsonObject)entry!).ToArray();

        Assert.Equal(
            [
                "all", "future", "missing", "existing", "recent", "pilot", "firstSeason",
                "latestSeason", "monitorSpecials", "unmonitorSpecials", "none",
            ],
            options.Select(option => option["key"]!.GetValue<string>()));

        // Rendered to a user as the key itself, because that generation ships no sentence for them.
        Assert.Equal(
            ["recent", "pilot", "monitorSpecials", "unmonitorSpecials"],
            options
                .Where(option => option["localized"] is null)
                .Select(option => option["key"]!.GetValue<string>()));

        var named = options.ToDictionary(
            option => option["key"]!.GetValue<string>(),
            option => option["localized"]?.GetValue<string>(),
            StringComparer.Ordinal);
        Assert.Equal("All Scenes", named["all"]);
        Assert.Equal("Future Scenes", named["future"]);

        var composed = Enum.GetValues<MonitorScope>()
            .Select(scope => (JsonObject)ComposedV2Body.Of(V2BodyProjector.AddStudio(
                SiteEntityId,
                scope,
                new AddDefaults(1, "/config/library")))["addOptions"]!)
            .Select(options2 => options2["monitor"]!.GetValue<string>())
            .Order()
            .ToArray();
        Assert.Equal(["all", "future"], composed);
    }

    private static void AssertOnlyTheFlagChanged(JsonObject before, JsonObject after)
    {
        Assert.Equal(
            before.Select(member => member.Key).Order(),
            after.Select(member => member.Key).Order());
        Assert.NotEqual(
            before["monitored"]!.GetValue<bool>(), after["monitored"]!.GetValue<bool>());

        foreach (var member in before.Where(member => member.Key != "monitored"))
        {
            Assert.Equal(
                member.Value?.ToJsonString(),
                after[member.Key]?.ToJsonString());
        }
    }

    private static bool Declares(JsonObject schemas, string schema, string property)
        => ((JsonObject)schemas[schema]!)["properties"] is JsonObject properties
            && properties.ContainsKey(property);

    private static IReadOnlyList<string> Keys(string fixtureName, string member)
        => [.. Array(fixtureName).Select(entry => ((JsonObject)entry!)[member]!.GetValue<string>())];

    private static JsonObject Object(string fixtureName)
        => Assert.IsType<JsonObject>(JsonNode.Parse(ProbeFixtures.Read(fixtureName)));

    private static JsonArray Array(string fixtureName)
        => Assert.IsType<JsonArray>(JsonNode.Parse(ProbeFixtures.Read(fixtureName)));

    private static JsonArray Array(JsonObject document, string member)
        => Assert.IsType<JsonArray>(document[member]);
}
