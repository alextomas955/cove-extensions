using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Every body v2 is sent for a monitor, an unmonitor or a scope change, and the reading of the list
/// that says what the instance holds.
/// </summary>
/// <remarks>
/// This generation publishes no contract at all, so every shape asserted here is a hand transcription
/// of a measurement rather than a derivation from a document. The expected values are written out by
/// hand for that reason: one computed from the composer would agree with it whatever either said.
/// <para>
/// Nothing here reads a status: this generation answers a body whose fields it dropped with a created
/// status and an echo, so every case is on the parsed shape.
/// </para>
/// </remarks>
public sealed class V2BodyProjectorTests
{
    /// <summary>The fields a flag flip must leave alone, because the user owns each of them.</summary>
    private static readonly string[] FieldsAFlagFlipMustNotCarry =
        ["qualityProfileId", "rootFolderPath", "tags", "monitorNewItems", "seasons", "addOptions"];

    private static readonly AddDefaults Defaults = new(1, "/config/library");

    /// <summary>Each scope this product expresses composes this generation's own key for it.</summary>
    [Theory]
    [InlineData(MonitorScope.FutureScenes, "future")]
    [InlineData(MonitorScope.AllScenes, "all")]
    public void TheAddCarriesTheScopeKeyThisGenerationSpellsItWith(MonitorScope scope, string key)
    {
        var body = ComposedV2Body.Of(
            V2BodyProjector.AddStudio(3372, scope, Defaults));

        Assert.Equal(key, ((JsonObject)body["addOptions"]!)["monitor"]!.GetValue<string>());
    }

    /// <summary>
    /// No key beyond those two is composable, over every scope this product expresses.
    /// </summary>
    /// <remarks>
    /// This generation's own dropdown offers nine more, four of which it renders to a user as raw
    /// localization keys. Mimicry stops there.
    /// </remarks>
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

    /// <summary>
    /// Both of this generation's suppression spellings are present as members and both are false.
    /// </summary>
    /// <remarks>
    /// Presence is asserted apart from the value, and v2's pair is not v3's: a rule stated in v3's
    /// spellings leaves every body here unguarded.
    /// </remarks>
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

    /// <summary>Whisparr v3's spellings reach no body composed here.</summary>
    [Fact]
    public void NoAddCarriesTheOtherGenerationsSuppressionSpellings()
    {
        var body = ComposedV2Body
            .Of(V2BodyProjector.AddStudio(3372, MonitorScope.FutureScenes, Defaults))
            .ToJsonString();

        Assert.DoesNotContain("searchOnAdd", body, StringComparison.Ordinal);
        Assert.DoesNotContain("searchForMovie", body, StringComparison.Ordinal);
    }

    /// <summary>The add is this generation's own form, field for field.</summary>
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

    /// <summary>
    /// No composed add names a quality profile the instance would never act on.
    /// </summary>
    /// <remarks>
    /// This generation refuses a zero with a validation failure naming the property, and v3
    /// accepts it and echoes it back. The stop is this product's own so neither generation's behaviour
    /// is what the guarantee rests on.
    /// </remarks>
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

    /// <summary>An add with no library root is refused before it can be composed.</summary>
    [Fact]
    public void AnAddWithNoLibraryRootIsRefused()
        => Assert.Throws<ArgumentException>(
            () => V2BodyProjector.AddStudio(
                3372, MonitorScope.AllScenes, new AddDefaults(1, "  ")));

    /// <summary>The flag flip names the entity and the flag, and says nothing else at all.</summary>
    [Fact]
    public void TheFlagFlipCarriesTheIdArrayAndTheFlagAndNothingElse()
    {
        var body = ComposedV2Body.Of(V2BodyProjector.SetMonitored(1, monitored: false));

        Assert.Equal(["monitored", "seriesIds"], body.Select(member => member.Key).Order());
        Assert.Equal([1], Assert.IsType<JsonArray>(body["seriesIds"]).Select(id => id!.GetValue<int>()));
        Assert.False(body["monitored"]!.GetValue<bool>());
        Assert.All(FieldsAFlagFlipMustNotCarry, field => Assert.False(body.ContainsKey(field)));
    }

    /// <summary>
    /// The scope change nests the entity inside an array of objects rather than naming it as a scalar.
    /// </summary>
    /// <remarks>
    /// The route answers a body it cannot read with a server failure and an empty body, so the shape is
    /// the whole of what makes the request expressible.
    /// </remarks>
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

    /// <summary>
    /// A scope value this product does not express throws rather than resolving to one it does.
    /// </summary>
    /// <remarks>
    /// Resolving to a default would resolve to whichever key came first, and one of the two marks a
    /// whole back catalogue wanted.
    /// </remarks>
    [Fact]
    public void AnUnrecognisedScopeThrowsRatherThanResolvingToAnyScope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V2BodyProjector.AddStudio(3372, (MonitorScope)7, Defaults));
        Assert.Throws<ArgumentOutOfRangeException>(() => V2BodyProjector.SetScope(1, (MonitorScope)7));
    }

    /// <summary>What the instance holds is read out of its own list, by the site number.</summary>
    /// <remarks>
    /// The row the list answers with is the only place the instance-side id appears, so the match is
    /// what carries it.
    /// </remarks>
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
