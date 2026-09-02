using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The external facts this product's monitoring rests on, pinned against documents the two measured
/// builds produced.
/// </summary>
/// <remarks>
/// The captured documents are INPUTS. Every expected value below was written out by hand, because one
/// computed from the document it checks would agree with that document whatever either said.
/// <para>
/// Nothing here reads a status. One of these generations answers an identifier it does not know with a
/// success and an empty list, and answers a body whose fields it dropped with a created status and an
/// echo showing them gone, so a status is not evidence about either. Every assertion is on the parsed
/// shape or on the content of a document.
/// </para>
/// <para>
/// The older generation publishes no contract at all, so every shape it answers with is a hand
/// transcription that survives only here: the ledger these were taken from is local and unversioned.
/// </para>
/// <para>
/// WHAT AN IMAGE BUMP MEANS: a pin that goes red is reporting that the fact changed, and the fact is
/// then re-measured against the new image and the code re-decided. The fixture is never edited to
/// match. Every fixture file names the build it came from for exactly that reason, so a stale one is
/// visible rather than silently authoritative.
/// </para>
/// </remarks>
public sealed class MonitorBodyPinTests
{
    private const string V3Build = "3.3.8.1097";
    private const string V2Build = "2.2.0.231";

    private const string V3StudioFixture = "whisparr-v3-3.3.8.1097-studio-resource.json";
    private const string V3StudioAfterEditorFixture = "whisparr-v3-3.3.8.1097-studio-after-editor.json";
    private const string V3MinimalRefusalFixture = "whisparr-v3-3.3.8.1097-studio-minimal-refusal.json";
    private const string V3MediaManagementFixture = "whisparr-v3-3.3.8.1097-media-management.json";
    private const string V3SchemasFixture = "whisparr-v3-3.3.8.1097-resource-schemas.json";
    private const string V3ImportModesFixture = "whisparr-v3-3.3.8.1097-import-modes.json";
    private const string V3CommandsFixture = "whisparr-v3-3.3.8.1097-command-payloads.json";

    private const string V2LookupFixture = "whisparr-v2-2.2.0.231-series-lookup.json";
    private const string V2LookupEmptyFixture = "whisparr-v2-2.2.0.231-series-lookup-empty.json";
    private const string V2AddRefusalFixture = "whisparr-v2-2.2.0.231-series-add-refusal.json";
    private const string V2SeriesFixture = "whisparr-v2-2.2.0.231-series-resource.json";
    private const string V2SeriesAfterEditorFixture = "whisparr-v2-2.2.0.231-series-after-editor.json";
    private const string V2MediaManagementFixture = "whisparr-v2-2.2.0.231-media-management.json";
    private const string V2SeasonPassRefusalFixture = "whisparr-v2-2.2.0.231-seasonpass-no-body-refusal.json";
    private const string V2QueueFixture = "whisparr-v2-2.2.0.231-queue.json";
    private const string V2MonitorOptionsFixture = "whisparr-v2-2.2.0.231-monitor-options.json";
    private const string V2ImportModesFixture = "whisparr-v2-2.2.0.231-import-modes.json";
    private const string V2CommandsFixture = "whisparr-v2-2.2.0.231-command-payloads.json";

    /// <summary>The identifier the entity that was added is named by on the newer generation.</summary>
    private const string StudioForeignId = "44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e";

    /// <summary>The numeric identifier the older generation's lookup answered for one entity.</summary>
    private const int SiteEntityId = 3372;

    private static readonly AddDefaults Defaults = new(4, "/config/library");

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The newer generation answers an add missing either of its two NOT NULL columns with a raw
    /// database message and a stack trace, so a composed add always carries both.
    /// </summary>
    /// <remarks>
    /// Claimed of build 3.3.8.1097, from the document an add carrying only the identifier produced.
    /// The add path has no validation rule set in front of it, so the database constraint is the
    /// validator and the answer is unreadable. The second member of that answer is a stack trace: this
    /// asserts it is one, which is why nothing anywhere reads it.
    /// </remarks>
    [Fact]
    public void TheNewerGenerationRefusesAnAddMissingEitherColumnItsDatabaseRequires()
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

        var composed = V3BodyProjector.AddStudio(
            StudioForeignId, MonitorScope.AllScenes, Defaults, Now);
        Assert.True(composed.ContainsKey("rootFolderPath"), $"the add omits a column {V3Build} requires");
        Assert.True(composed.ContainsKey("tags"), $"the add omits a column {V3Build} requires");
    }

    /// <summary>
    /// The add-time date gate is held in a different spelling from the one it is sent in, so comparing
    /// the two as strings reports a change that did not happen.
    /// </summary>
    /// <remarks>
    /// Claimed of build 3.3.8.1097. Sent as an instant and held as a date, and both spellings are
    /// pinned because a caller that compares them has to compare dates.
    /// </remarks>
    [Fact]
    public void TheDateGateIsHeldInADifferentSpellingFromTheOneItIsSentIn()
    {
        var sent = V3BodyProjector
            .AddStudio(StudioForeignId, MonitorScope.FutureScenes, Defaults, Now)["afterDate"]!
            .GetValue<string>();
        var held = Object(V3StudioFixture)["afterDate"]!.GetValue<string>();

        Assert.Equal("2026-09-02T00:00:00Z", sent);
        Assert.Equal("2026-09-02", held);
        Assert.NotEqual(sent, held);
        Assert.Equal(
            DateTimeOffset.Parse(sent, CultureInfo.InvariantCulture).Date,
            DateTime.Parse(held, CultureInfo.InvariantCulture).Date);
    }

    /// <summary>
    /// The newer generation accepts a quality profile it can never act on and echoes it back, so the
    /// stop is this product's own.
    /// </summary>
    /// <remarks>
    /// Claimed of build 3.3.8.1097, from the document an add carrying a zero produced. An entity
    /// stored under it monitors and then never acquires, and nothing in the answer says so.
    /// </remarks>
    [Fact]
    public void TheNewerGenerationAcceptsAProfileItCanNeverActOnAndThisProductDoesNot()
    {
        Assert.Equal(0, Object(V3StudioFixture)["qualityProfileId"]!.GetValue<int>());
        Assert.True(Object(V3StudioFixture)["monitored"]!.GetValue<bool>());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => V3BodyProjector.AddStudio(
                StudioForeignId, MonitorScope.AllScenes, new AddDefaults(0, "/config/library"), Now));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V2BodyProjector.AddStudio(
                SiteEntityId, "Vixen", "vixen", MonitorScope.AllScenes, new AddDefaults(0, "/config/library")));
    }

    /// <summary>
    /// A freshly added entity reports a catalogue of zero before anything has read one, so a count
    /// taken then would be a confident zero this product cannot support.
    /// </summary>
    /// <remarks>Claimed of build 3.3.8.1097, from the document a just-added entity read as.</remarks>
    [Fact]
    public void AFreshlyAddedEntityReportsNoCatalogueAtAll()
    {
        var studio = Object(V3StudioFixture);

        Assert.Equal(0, studio["sceneCount"]!.GetValue<int>());
        Assert.Equal(0, studio["totalSceneCount"]!.GetValue<int>());
    }

    /// <summary>
    /// The newer generation's editor route leaves every field the request does not name exactly as it
    /// was, which is what makes a two-key flag flip safe.
    /// </summary>
    /// <remarks>
    /// Claimed of build 3.3.8.1097, from the entity read before and after a flip carrying the id array
    /// and the flag. Compared member by member: the flag is the only difference.
    /// </remarks>
    [Fact]
    public void TheNewerGenerationsEditorLeavesEveryFieldTheRequestDoesNotName()
    {
        AssertOnlyTheFlagChanged(Object(V3StudioFixture), Object(V3StudioAfterEditorFixture));

        Assert.Equal(
            ["monitored", "studioIds"],
            V3BodyProjector.SetStudioMonitored(1, monitored: false)
                .Select(member => member.Key)
                .Order());
    }

    /// <summary>
    /// The add-time date gate is declared on one resource and on no other in the whole contract, so a
    /// future-only scope is not expressible for any other kind.
    /// </summary>
    /// <remarks>
    /// Claimed of build 3.3.8.1097, from the contract that build publishes. The editor resource does
    /// not declare it either, which is why a scope change is the one case the editor route cannot
    /// express.
    /// </remarks>
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

    /// <summary>
    /// Both generations offer the same three import modes, and neither offers one that registers a
    /// file without transferring it.
    /// </summary>
    /// <remarks>
    /// Claimed of builds 3.3.8.1097 and 2.2.0.231, transcribed from the interface bundle each image
    /// ships. The two arrays are compared with each other AND with a hand-written expectation, because
    /// comparing them only with each other would pass if both changed the same way.
    /// </remarks>
    [Fact]
    public void BothGenerationsOfferTheSameThreeImportModesAndNoInPlaceOne()
    {
        var expected = new[] { "chooseImportMode", "move", "copy" };
        var newer = Keys(V3ImportModesFixture, "key");
        var older = Keys(V2ImportModesFixture, "key");

        Assert.Equal(expected, newer);
        Assert.Equal(expected, older);
        Assert.Equal(newer, older);

        // The first is a placeholder the interface renders unselectable, so two are reachable.
        Assert.True(Array(V3ImportModesFixture)[0]!["disabled"]!.GetValue<bool>());
        Assert.Equal("HardlinkCopyFiles", Array(V3ImportModesFixture)[2]!["label"]!.GetValue<string>());
    }

    /// <summary>
    /// Both generations link a file into place rather than copying it, by default.
    /// </summary>
    /// <remarks>
    /// Claimed of builds 3.3.8.1097 and 2.2.0.231, from the configuration each answered with. It is a
    /// default rather than a guarantee, which is why it is read before acting rather than assumed.
    /// </remarks>
    [Fact]
    public void BothGenerationsLinkAFileIntoPlaceByDefault()
    {
        Assert.True(Object(V3MediaManagementFixture)["copyUsingHardlinks"]!.GetValue<bool>());
        Assert.True(Object(V2MediaManagementFixture)["copyUsingHardlinks"]!.GetValue<bool>());
    }

    /// <summary>
    /// The older generation's lookup answers a list that names each entity by a field it misnames, and
    /// never echoes the term it was asked under.
    /// </summary>
    /// <remarks>
    /// Claimed of build 2.2.0.231, from a real lookup answer. No member of the whole document is an
    /// identifier of the shape the library holds, which is why the correspondence between the stored
    /// identifier and the entity acted on rests on there being exactly one answer rather than on any
    /// field matching what was sent.
    /// <para>
    /// No element carries an instance-side identifier either. That is what makes "does the instance
    /// hold this" a second question rather than a member of this answer.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOlderGenerationsLookupNeverEchoesTheTermItWasAskedUnder()
    {
        var answered = Array(V2LookupFixture);

        Assert.NotEmpty(answered);
        Assert.All(
            answered,
            entry =>
            {
                var site = Assert.IsType<JsonObject>(entry);
                Assert.True(site.ContainsKey("tvdbId"), $"an entry names no identifier on {V2Build}");
                Assert.True(site.ContainsKey("titleSlug"), $"an entry names no slug on {V2Build}");
                Assert.False(site.ContainsKey("id"), $"an entry carries an instance id on {V2Build}");
            });

        Assert.Empty(
            Regex.Matches(
                ProbeFixtures.Read(V2LookupFixture),
                "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// A term the older generation's source does not know is answered with a success and an empty
    /// list, so nothing about the answer says the request was wrong.
    /// </summary>
    /// <remarks>
    /// Claimed of build 2.2.0.231. Three probes produced this one document byte for byte: an
    /// identifier of the shape the library holds that its source does not know, an identifier minted
    /// by the other generation's source, and the prefixed spelling. The prefixed form is the trap,
    /// because it looks more correct and reports nothing at all.
    /// </remarks>
    [Fact]
    public void ATermThatMatchesNothingIsAnsweredWithAnEmptyList()
    {
        Assert.Empty(Array(V2LookupEmptyFixture));
        Assert.Equal(
            V2LookupReading.NoMatch,
            V2LookupProjector.Resolve(ProbeFixtures.Read(V2LookupEmptyFixture)).Reading);
    }

    /// <summary>
    /// A term that names more than one entity is refused by this product rather than picked from.
    /// </summary>
    /// <remarks>
    /// Claimed of build 2.2.0.231, over a real answer holding many entities. Nothing in that answer
    /// says which of them the term meant.
    /// </remarks>
    [Fact]
    public void AnAnswerNamingMoreThanOneEntityIsRefusedRatherThanPickedFrom()
    {
        var resolution = V2LookupProjector.Resolve(ProbeFixtures.Read(V2LookupFixture));

        Assert.Equal(V2LookupReading.Ambiguous, resolution.Reading);
        Assert.Null(resolution.Site);
    }

    /// <summary>
    /// One answer names the entity by the misnamed numeric field and by its slug, and that is what the
    /// add is composed from.
    /// </summary>
    /// <remarks>
    /// Claimed of build 2.2.0.231. The single answer is taken out of the real document rather than
    /// invented, so what is resolved here is an entry that instance produced.
    /// </remarks>
    [Fact]
    public void OneAnswerNamesTheEntityByTheFieldThisGenerationMisnames()
    {
        var one = new JsonArray(
            Array(V2LookupFixture)
                .Select(entry => (JsonObject)entry!.DeepClone())
                .Single(site => site["tvdbId"]!.GetValue<int>() == SiteEntityId));

        var resolution = V2LookupProjector.Resolve(one.ToJsonString());

        Assert.Equal(V2LookupReading.Resolved, resolution.Reading);
        var site = Assert.IsType<V2Site>(resolution.Site);
        Assert.Equal(SiteEntityId, site.EntityId);
        Assert.Equal("Vixen", site.Title);
        Assert.Equal("vixen", site.TitleSlug);

        Assert.Equal(
            SiteEntityId,
            V2BodyProjector
                .AddStudio(site.EntityId, site.Title, site.TitleSlug, MonitorScope.FutureScenes, new AddDefaults(1, "/config/library"))["tvdbId"]!
                .GetValue<int>());
    }

    /// <summary>
    /// The older generation refuses both a zero quality profile and a library root it has not been
    /// given, and names each by property and by validator.
    /// </summary>
    /// <remarks>
    /// Claimed of build 2.2.0.231. The newer generation accepts both, so neither generation's own
    /// behaviour is what the product's guarantee rests on: the profile is stopped before it is sent,
    /// and the root is read from the instance rather than chosen.
    /// </remarks>
    [Fact]
    public void TheOlderGenerationRefusesAProfileAndARootTheNewerOneAccepts()
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

    /// <summary>
    /// The older generation's editor route leaves every field the request does not name exactly as it
    /// was, including the flag on every one of the entity's catalogue years.
    /// </summary>
    /// <remarks>
    /// Claimed of build 2.2.0.231, from the entity read before and after a flip carrying the id array
    /// and the flag. The per-year flags are what a wider body would silently overwrite, and the user
    /// owns every one of them.
    /// </remarks>
    [Fact]
    public void TheOlderGenerationsEditorLeavesEveryPerYearFlagAsItWas()
    {
        AssertOnlyTheFlagChanged(Object(V2SeriesFixture), Object(V2SeriesAfterEditorFixture));

        Assert.Equal(
            ["monitored", "seriesIds"],
            V2BodyProjector.SetMonitored(1, monitored: false).Select(member => member.Key).Order());
    }

    /// <summary>
    /// The entity's catalogue is divided into YEARS on the older generation, whatever its own wire
    /// field is called.
    /// </summary>
    /// <remarks>
    /// Claimed of build 2.2.0.231. No wording a user reads may carry that field's name, which is why
    /// nothing composed on this path spells it.
    /// </remarks>
    [Fact]
    public void TheOlderGenerationsCatalogueIsDividedIntoYears()
    {
        var divisions = Array(Object(V2SeriesFixture), "seasons")
            .Select(entry => ((JsonObject)entry!)["seasonNumber"]!.GetValue<int>())
            .ToArray();

        Assert.NotEmpty(divisions);
        Assert.All(divisions, division => Assert.InRange(division, 2000, 2100));
    }

    /// <summary>
    /// The scope route on the older generation refuses a request with no body, so naming the verb is
    /// not enough to probe it and the body is what makes it work.
    /// </summary>
    /// <remarks>Claimed of build 2.2.0.231, from the answer a bodyless request produced.</remarks>
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
        Assert.IsType<JsonArray>(V2BodyProjector.SetScope(1, MonitorScope.AllScenes)["series"]);
    }

    /// <summary>
    /// Nothing the measured monitoring sequence did put anything in the acquisition queue.
    /// </summary>
    /// <remarks>
    /// Claimed of build 2.2.0.231, from the queue as it read after an add, a flag flip and two scope
    /// changes. The composed-body assertions are what carry the never-acquire guarantee; this is the
    /// instance agreeing with them once.
    /// </remarks>
    [Fact]
    public void TheMeasuredSequencePutNothingInTheAcquisitionQueue()
    {
        var queue = Object(V2QueueFixture);

        Assert.Equal(0, queue["totalRecords"]!.GetValue<int>());
        Assert.Empty(Assert.IsType<JsonArray>(queue["records"]));
    }

    /// <summary>
    /// A command payload names its entities in an array on one generation and as a scalar on the
    /// other, and a payload written in the wrong one is accepted and does nothing.
    /// </summary>
    /// <remarks>
    /// Claimed of builds 3.3.8.1097 and 2.2.0.231, transcribed from the interface bundle each image
    /// ships. All the shapes either generation uses are named, because the split is per generation and
    /// not per command.
    /// </remarks>
    [Fact]
    public void TheCommandPayloadsSplitBetweenAnArrayAndAScalar()
    {
        var newer = Array(V3CommandsFixture)
            .ToDictionary(
                entry => ((JsonObject)entry!)["name"]!.GetValue<string>(),
                entry => (JsonObject)entry!,
                StringComparer.Ordinal);
        var older = Array(V2CommandsFixture)
            .ToDictionary(
                entry => ((JsonObject)entry!)["name"]!.GetValue<string>(),
                entry => (JsonObject)entry!,
                StringComparer.Ordinal);

        Assert.Equal(["PerformersSearch", "RefreshStudios", "StudiosSearch"], newer.Keys.Order());
        Assert.Equal(["RefreshSeries", "SeriesSearch"], older.Keys.Order());

        Assert.IsType<JsonArray>(newer["StudiosSearch"]["studioIds"]);
        Assert.IsType<JsonArray>(newer["RefreshStudios"]["studioIds"]);
        Assert.IsType<JsonArray>(newer["PerformersSearch"]["performerIds"]);

        Assert.Null(older["SeriesSearch"]["seriesId"] as JsonArray);
        Assert.Equal(1, older["SeriesSearch"]["seriesId"]!.GetValue<int>());
        Assert.Null(older["RefreshSeries"]["seriesId"] as JsonArray);
        Assert.Equal(1, older["RefreshSeries"]["seriesId"]!.GetValue<int>());
    }

    /// <summary>
    /// The older generation offers eleven monitor options, four of which it renders to a user as the
    /// key rather than a sentence, and this product composes two of the eleven.
    /// </summary>
    /// <remarks>
    /// Claimed of build 2.2.0.231, transcribed from the interface bundle and the localization file
    /// that image ships. The two this product uses are the two whose words are clean, and they are
    /// that generation's own words rather than this product's.
    /// </remarks>
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
            .Select(scope => (JsonObject)V2BodyProjector.AddStudio(
                SiteEntityId, "Vixen", "vixen", scope, new AddDefaults(1, "/config/library"))["addOptions"]!)
            .Select(options2 => options2["monitor"]!.GetValue<string>())
            .Order()
            .ToArray();
        Assert.Equal(["all", "future"], composed);
    }

    /// <summary>
    /// Every member but the flag is byte-identical across <paramref name="before"/> and
    /// <paramref name="after"/>.
    /// </summary>
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
