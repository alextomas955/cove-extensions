using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Every body the older generation is sent for a monitor, an unmonitor or a scope change, and the
/// reading of the lookup that names the entity those bodies are about.
/// </summary>
/// <remarks>
/// This generation publishes no contract at all, so every shape asserted here is a hand transcription
/// of a measurement rather than a derivation from a document. The expected values are written out by
/// hand for that reason: one computed from the composer would agree with it whatever either said.
/// <para>
/// The lookup answers with a success and an empty array for an identifier it does not know, so
/// nothing here reads a status: every case is on the parsed shape.
/// </para>
/// </remarks>
public sealed class V2BodyProjectorTests
{
    /// <summary>A stored identifier of the shape this generation's source mints.</summary>
    private const string StoredIdentifier = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    /// <summary>The fields a flag flip must leave alone, because the user owns each of them.</summary>
    private static readonly string[] FieldsAFlagFlipMustNotCarry =
        ["qualityProfileId", "rootFolderPath", "tags", "monitorNewItems", "seasons", "addOptions"];

    /// <summary>One site as the lookup answers with it, named by the field this generation misnames.</summary>
    private const string OneSite = """
        [{"tvdbId":3372,"title":"Vixen","titleSlug":"vixen","year":2016,"status":"continuing"}]
        """;

    private static readonly AddDefaults Defaults = new(1, "/config/library");

    /// <summary>
    /// The lookup term is the stored identifier, unchanged and unprefixed.
    /// </summary>
    /// <remarks>
    /// The prefixed spelling is answered with a success and an empty array, so a term carrying one
    /// matches nothing and reports no failure of any kind.
    /// </remarks>
    [Fact]
    public void TheLookupTermIsTheStoredIdentifierCharacterForCharacter()
    {
        var term = V2BodyProjector.LookupTerm(StoredIdentifier);

        Assert.Equal(StoredIdentifier, term);
        Assert.DoesNotContain("tpdb", term, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":", term, StringComparison.Ordinal);
    }

    /// <summary>Each scope this product expresses composes this generation's own key for it.</summary>
    [Theory]
    [InlineData(MonitorScope.FutureScenes, "future")]
    [InlineData(MonitorScope.AllScenes, "all")]
    public void TheAddCarriesTheScopeKeyThisGenerationSpellsItWith(MonitorScope scope, string key)
    {
        var body = V2BodyProjector.AddStudio(3372, "Vixen", "vixen", scope, Defaults);

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
            .Select(scope => V2BodyProjector.AddStudio(3372, "Vixen", "vixen", scope, Defaults))
            .Select(body => ((JsonObject)body["addOptions"]!)["monitor"]!.GetValue<string>())
            .Order()
            .ToArray();

        Assert.Equal(["all", "future"], composed);
    }

    /// <summary>
    /// Both of this generation's suppression spellings are present as members and both are false.
    /// </summary>
    /// <remarks>
    /// Presence is asserted apart from the value, and this generation's pair is not the newer one's:
    /// a rule stated in the newer spellings leaves every body here unguarded.
    /// </remarks>
    [Theory]
    [InlineData(MonitorScope.FutureScenes)]
    [InlineData(MonitorScope.AllScenes)]
    public void EveryAddCarriesBothOfThisGenerationsSuppressionSpellingsPresentAndFalse(MonitorScope scope)
    {
        var options = (JsonObject)V2BodyProjector
            .AddStudio(3372, "Vixen", "vixen", scope, Defaults)["addOptions"]!;

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

    /// <summary>The newer generation's spellings reach no body composed here.</summary>
    [Fact]
    public void NoAddCarriesTheOtherGenerationsSuppressionSpellings()
    {
        var body = V2BodyProjector
            .AddStudio(3372, "Vixen", "vixen", MonitorScope.FutureScenes, Defaults)
            .ToJsonString();

        Assert.DoesNotContain("searchOnAdd", body, StringComparison.Ordinal);
        Assert.DoesNotContain("searchForMovie", body, StringComparison.Ordinal);
    }

    /// <summary>The add is this generation's own form, field for field.</summary>
    [Fact]
    public void TheAddCarriesEveryFieldThisGenerationsOwnFormSends()
    {
        var body = V2BodyProjector.AddStudio(3372, "Vixen", "vixen", MonitorScope.AllScenes, Defaults);

        Assert.Equal(
            [
                "addOptions", "monitored", "monitorNewItems", "qualityProfileId", "rootFolderPath",
                "seasons", "seriesType", "tags", "title", "titleSlug", "tvdbId",
            ],
            body.Select(member => member.Key).Order());
        Assert.Equal(3372, body["tvdbId"]!.GetValue<int>());
        Assert.Equal("Vixen", body["title"]!.GetValue<string>());
        Assert.Equal("vixen", body["titleSlug"]!.GetValue<string>());
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
    /// This generation refuses a zero with a validation failure naming the property, and the newer one
    /// accepts it and echoes it back. The stop is this product's own so neither generation's behaviour
    /// is what the guarantee rests on.
    /// </remarks>
    [Fact]
    public void AnAddComposedWithAProfileTheInstanceWouldNeverActOnIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V2BodyProjector.AddStudio(
                3372, "Vixen", "vixen", MonitorScope.AllScenes, new AddDefaults(0, "/config/library")));

        Assert.All(
            Enum.GetValues<MonitorScope>(),
            scope => Assert.True(
                V2BodyProjector.AddStudio(3372, "Vixen", "vixen", scope, Defaults)["qualityProfileId"]!
                    .GetValue<int>() > 0));
    }

    /// <summary>An add with no library root is refused before it can be composed.</summary>
    [Fact]
    public void AnAddWithNoLibraryRootIsRefused()
        => Assert.Throws<ArgumentException>(
            () => V2BodyProjector.AddStudio(
                3372, "Vixen", "vixen", MonitorScope.AllScenes, new AddDefaults(1, "  ")));

    /// <summary>The flag flip names the entity and the flag, and says nothing else at all.</summary>
    [Fact]
    public void TheFlagFlipCarriesTheIdArrayAndTheFlagAndNothingElse()
    {
        var body = V2BodyProjector.SetMonitored(1, monitored: false);

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
        var body = V2BodyProjector.SetScope(1, scope);

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
            () => V2BodyProjector.AddStudio(3372, "Vixen", "vixen", (MonitorScope)7, Defaults));
        Assert.Throws<ArgumentOutOfRangeException>(() => V2BodyProjector.SetScope(1, (MonitorScope)7));
    }

    /// <summary>
    /// One result names the entity by the field this generation misnames, and never by the identifier
    /// that was asked about.
    /// </summary>
    /// <remarks>
    /// The response echoes nothing of the term, so the correspondence rests on there being exactly one
    /// answer rather than on any field matching what was sent.
    /// </remarks>
    [Fact]
    public void OneResultNamesTheEntityByTheFieldThisGenerationMisnames()
    {
        var resolution = V2LookupProjector.Resolve(OneSite);

        Assert.Equal(V2LookupReading.Resolved, resolution.Reading);
        Assert.Equal(MonitorRefusalKind.None, V2LookupProjector.RefusalFor(resolution.Reading));
        var site = Assert.IsType<V2Site>(resolution.Site);
        Assert.Equal(3372, site.EntityId);
        Assert.Equal("Vixen", site.Title);
        Assert.Equal("vixen", site.TitleSlug);
        Assert.DoesNotContain(StoredIdentifier, OneSite, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// More than one result is a refusal, never a pick of the first.
    /// </summary>
    /// <remarks>
    /// Nothing in the answer says which of them the identifier meant, so acting on either would act on
    /// an entity nobody named.
    /// </remarks>
    [Fact]
    public void MoreThanOneResultIsARefusalRatherThanAFirstResultPick()
    {
        const string twoSites = """
            [{"tvdbId":3372,"title":"Vixen","titleSlug":"vixen"},
             {"tvdbId":36826,"title":"Vixen Media Group","titleSlug":"vixen-media-group"}]
            """;

        var resolution = V2LookupProjector.Resolve(twoSites);

        Assert.Equal(V2LookupReading.Ambiguous, resolution.Reading);
        Assert.Null(resolution.Site);
        Assert.Equal(
            MonitorRefusalKind.InstanceRefused, V2LookupProjector.RefusalFor(resolution.Reading));
    }

    /// <summary>No result at all is the no-identity refusal.</summary>
    [Fact]
    public void NoResultAtAllIsTheNoIdentityRefusal()
    {
        var resolution = V2LookupProjector.Resolve("[]");

        Assert.Equal(V2LookupReading.NoMatch, resolution.Reading);
        Assert.Null(resolution.Site);
        Assert.Equal(
            MonitorRefusalKind.NoIdentityInThisNamespace,
            V2LookupProjector.RefusalFor(resolution.Reading));
    }

    /// <summary>An answer that is not a list of results at all is a refusal, not an absence.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("<!DOCTYPE html>")]
    [InlineData("""{"message":"not a list"}""")]
    [InlineData("""[{"title":"Vixen","titleSlug":"vixen"}]""")]
    [InlineData("""[{"tvdbId":0,"title":"Vixen","titleSlug":"vixen"}]""")]
    public void AnAnswerThatNamesNoEntityIsARefusalRatherThanAnAbsence(string body)
    {
        var resolution = V2LookupProjector.Resolve(body);

        Assert.Equal(V2LookupReading.Unreadable, resolution.Reading);
        Assert.Null(resolution.Site);
        Assert.Equal(
            MonitorRefusalKind.InstanceRefused, V2LookupProjector.RefusalFor(resolution.Reading));
    }

    /// <summary>
    /// What the instance holds is read out of its own listing by the identifier the lookup named.
    /// </summary>
    /// <remarks>
    /// The lookup answers with no instance-side id until the entity has been added, so whether it is
    /// held is a second question and the listing is what answers it.
    /// </remarks>
    [Fact]
    public void TheHeldEntryIsTheListedEntityCarryingTheIdentifierTheLookupNamed()
    {
        const string listed = """
            [{"id":1,"tvdbId":3372,"title":"Vixen","monitored":true},
             {"id":2,"tvdbId":247,"title":"Tushy Raw","monitored":false}]
            """;

        var held = V2LookupProjector.HeldEntry(listed, 3372);

        Assert.NotNull(held);
        Assert.Equal(1, held["id"]!.GetValue<int>());
        Assert.True(held["monitored"]!.GetValue<bool>());
        Assert.Null(V2LookupProjector.HeldEntry(listed, 36826));
        Assert.Null(V2LookupProjector.HeldEntry("[]", 3372));
        Assert.Null(V2LookupProjector.HeldEntry("<!DOCTYPE html>", 3372));
    }
}
