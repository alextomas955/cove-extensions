using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// v2 publishes no contract document, so every shape asserted here is transcribed from a
// measurement. The expected values are written out by hand for that reason: one computed from the
// composer would agree with it whatever either said.
//
// Nothing here reads a status. v2 answers a body whose fields it dropped with a created status and
// an echo, so every case asserts on the parsed shape.
public sealed class V2BodyProjectorTests
{
    // Fields a flag flip leaves alone, because the user owns each of them.
    private static readonly string[] FieldsAFlagFlipMustNotCarry =
        ["qualityProfileId", "rootFolderPath", "tags", "monitorNewItems", "seasons", "addOptions"];

    private static readonly AddDefaults Defaults = new(1, "/config/library");

    [Theory]
    [InlineData(MonitorScope.FutureScenes, "future")]
    [InlineData(MonitorScope.AllScenes, "all")]
    public void TheAddCarriesTheScopeKeyThisGenerationSpellsItWith(MonitorScope scope, string key)
    {
        var body = ComposedV2Body.Of(
            V2BodyProjector.AddStudio(3372, scope, Defaults));

        Assert.Equal(key, ((JsonObject)body["addOptions"]!)["monitor"]!.GetValue<string>());
    }

    // v2's own dropdown offers nine more monitor keys, four of which it renders as raw localization
    // keys. This product composes only these two.
    [Fact]
    public void NoMonitorKeyBeyondTheTwoThisProductExpressesIsEverComposed()
    {
        var composed = Enum.GetValues<MonitorScope>()
            .Select(scope => ComposedV2Body.Of(
                V2BodyProjector.AddStudio(3372, scope, Defaults)))
            .Select(body => ((JsonObject)body["addOptions"]!)["monitor"]!.GetValue<string>())
            .Order()
            .ToArray();

        Assert.Equal(["all", "future"], composed);
    }

    // Presence is asserted apart from the value. v2's suppression pair is not v3's, so a rule
    // stated in v3's spellings leaves every body here unguarded.
    [Theory]
    [InlineData(MonitorScope.FutureScenes)]
    [InlineData(MonitorScope.AllScenes)]
    public void EveryAddCarriesBothOfThisGenerationsSuppressionSpellingsPresentAndFalse(MonitorScope scope)
    {
        var options = (JsonObject)ComposedV2Body
            .Of(V2BodyProjector.AddStudio(3372, scope, Defaults))["addOptions"]!;

        Assert.True(options.ContainsKey("searchForMissingEpisodes"));
        Assert.True(options.ContainsKey("searchForCutoffUnmetEpisodes"));
        Assert.Equal(
            new[] { false, false },
            new[]
            {
                options["searchForMissingEpisodes"]!.GetValue<bool>(),
                options["searchForCutoffUnmetEpisodes"]!.GetValue<bool>(),
            });
    }

    [Fact]
    public void NoAddCarriesTheOtherGenerationsSuppressionSpellings()
    {
        var body = ComposedV2Body
            .Of(V2BodyProjector.AddStudio(3372, MonitorScope.FutureScenes, Defaults))
            .ToJsonString();

        Assert.DoesNotContain("searchOnAdd", body, StringComparison.Ordinal);
        Assert.DoesNotContain("searchForMovie", body, StringComparison.Ordinal);
    }

    // The key set is v2's own add form, field for field.
    [Fact]
    public void TheAddCarriesEveryFieldThisGenerationsOwnFormSends()
    {
        var body = ComposedV2Body.Of(
            V2BodyProjector.AddStudio(3372, MonitorScope.AllScenes, Defaults));

        Assert.Equal(
            [
                "addOptions", "monitored", "monitorNewItems", "qualityProfileId", "rootFolderPath",
                "seasons", "seriesType", "tags", "title", "titleSlug", "tvdbId",
            ],
            body.Select(member => member.Key).Order());
        Assert.Equal(3372, body["tvdbId"]!.GetValue<int>());

        // The instance refuses an add carrying no title and discards the value of the one it is
        // given, resolving the site's real title and its slug from the number alone.
        Assert.Equal("3372", body["title"]!.GetValue<string>());

        // The member the generated resource cannot leave off the document. Null is its absence on
        // the wire, not a value this product chose.
        Assert.Null(body["titleSlug"]);
        Assert.Equal(1, body["qualityProfileId"]!.GetValue<int>());
        Assert.Equal("/config/library", body["rootFolderPath"]!.GetValue<string>());
        Assert.True(body["monitored"]!.GetValue<bool>());
        Assert.Equal("all", body["monitorNewItems"]!.GetValue<string>());
        Assert.Equal("standard", body["seriesType"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(body["seasons"]));
        Assert.Empty(Assert.IsType<JsonArray>(body["tags"]));
    }

    // v2 refuses a zero profile id with a validation failure naming the property and v3 accepts it
    // and echoes it back, so the refusal is this product's own and rests on neither.
    [Fact]
    public void AnAddComposedWithAProfileTheInstanceWouldNeverActOnIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V2BodyProjector.AddStudio(
                3372, MonitorScope.AllScenes, new AddDefaults(0, "/config/library")));

        Assert.All(
            Enum.GetValues<MonitorScope>(),
            scope => Assert.True(
                ComposedV2Body.Of(V2BodyProjector.AddStudio(
                    3372, scope, Defaults))["qualityProfileId"]!
                    .GetValue<int>() > 0));
    }

    [Fact]
    public void AnAddWithNoLibraryRootIsRefused()
        => Assert.Throws<ArgumentException>(
            () => V2BodyProjector.AddStudio(
                3372, MonitorScope.AllScenes, new AddDefaults(1, "  ")));

    [Fact]
    public void TheFlagFlipCarriesTheIdArrayAndTheFlagAndNothingElse()
    {
        var body = ComposedV2Body.Of(V2BodyProjector.SetMonitored(1, monitored: false));

        Assert.Equal(["monitored", "seriesIds"], body.Select(member => member.Key).Order());
        Assert.Equal([1], Assert.IsType<JsonArray>(body["seriesIds"]).Select(id => id!.GetValue<int>()));
        Assert.False(body["monitored"]!.GetValue<bool>());
        Assert.All(FieldsAFlagFlipMustNotCarry, field => Assert.False(body.ContainsKey(field)));
    }

    // The v2 route answers a body it cannot read with a server failure and an empty body, so the
    // nested array shape is the only expressible form of the request.
    [Theory]
    [InlineData(MonitorScope.FutureScenes, "future")]
    [InlineData(MonitorScope.AllScenes, "all")]
    public void TheScopeChangeNestsTheIdInsideAnArrayOfObjects(MonitorScope scope, string key)
    {
        var body = ComposedV2Body.Of(V2BodyProjector.SetScope(1, scope));

        Assert.Equal(["monitoringOptions", "series"], body.Select(member => member.Key).Order());
        var named = Assert.IsType<JsonArray>(body["series"]);
        Assert.Equal(1, Assert.IsType<JsonObject>(Assert.Single(named))["id"]!.GetValue<int>());
        Assert.Equal(key, ((JsonObject)body["monitoringOptions"]!)["monitor"]!.GetValue<string>());
    }

    // Falling back to a default would pick whichever key came first, and one of the two marks a
    // whole back catalogue wanted.
    [Fact]
    public void AnUnrecognisedScopeThrowsRatherThanResolvingToAnyScope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V2BodyProjector.AddStudio(3372, (MonitorScope)7, Defaults));
        Assert.Throws<ArgumentOutOfRangeException>(() => V2BodyProjector.SetScope(1, (MonitorScope)7));
    }

    // The listed row is the only place the instance-side id appears, so the match by site number is
    // what carries it.
    [Fact]
    public void TheHeldEntryIsTheListedRowCarryingTheSiteNumber()
    {
        const string listed = """
            [{"id":1,"tvdbId":3372,"title":"Vixen","monitored":true},
             {"id":2,"tvdbId":247,"title":"Tushy Raw","monitored":false}]
            """;

        var held = V2ListProjector.HeldEntry(listed, 3372);

        Assert.NotNull(held);
        Assert.Equal(1, held["id"]!.GetValue<int>());
        Assert.True(held["monitored"]!.GetValue<bool>());
        Assert.Null(V2ListProjector.HeldEntry(listed, 36826));
        Assert.Null(V2ListProjector.HeldEntry("[]", 3372));
        Assert.Null(V2ListProjector.HeldEntry("<!DOCTYPE html>", 3372));
    }
}
