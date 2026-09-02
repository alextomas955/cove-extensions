using System.Reflection;
using System.Text.Json.Nodes;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Every body the newer generation is sent for a monitor, an unmonitor, a scope change or a performer,
/// asserted on the composed object rather than through a client.
/// </summary>
/// <remarks>
/// The two editor bodies are asserted on their KEY SET and not only on their values. Every other
/// field of the editor resource is nullable and an omitted one is not applied, so a key appearing here
/// by accident is a value of the user's own that this product would overwrite.
/// <para>
/// Nothing here asserts what happens to a scene released exactly on the add-time boundary date.
/// Whether that date is inclusive is the instance's to classify, no behaviour of this product depends
/// on it, and an assertion either way would pin a fact this product does not own. The omission is
/// deliberate.
/// </para>
/// </remarks>
public sealed class V3BodyProjectorTests
{
    private const string StudioForeignId = "44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e";

    private const string PerformerForeignId = "9f0d6f27-1f3a-4a5f-8b21-6b2d3a5f9c10";

    /// <summary>The fields a flag flip must leave alone, because the user owns each of them.</summary>
    private static readonly string[] FieldsAFlagFlipMustNotCarry =
        ["qualityProfileId", "rootFolderPath", "tags", "afterDate", "searchOnAdd", "addOptions"];

    /// <summary>A studio as the instance holds one, with a catalogue and values a user chose.</summary>
    private const string HeldStudio = """
        {"id":4,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","title":"1000 Facials",
         "monitored":true,"afterDate":"2026-09-02","qualityProfileId":4,
         "rootFolderPath":"/config/library","tags":[7],"searchOnAdd":false,
         "sceneCount":4,"totalSceneCount":22}
        """;

    /// <summary>A studio as a freshly added one reads, before any catalogue refresh has run.</summary>
    private const string EmptyCatalogueStudio = """
        {"id":9,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","title":"Fresh",
         "monitored":true,"qualityProfileId":1,"rootFolderPath":"/config/library","tags":[],
         "sceneCount":0,"totalSceneCount":0}
        """;

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly AddDefaults Defaults = new(4, "/config/library");

    /// <summary>The studio flag flip names the entity and the flag, and says nothing else at all.</summary>
    [Fact]
    public void TheStudioFlagFlipCarriesTheIdArrayAndTheFlagAndNothingElse()
    {
        var body = V3BodyProjector.SetStudioMonitored(4, monitored: false);

        Assert.Equal(
            ["monitored", "studioIds"],
            body.Select(member => member.Key).Order());
        Assert.Equal([4], Assert.IsType<JsonArray>(body["studioIds"]).Select(id => id!.GetValue<int>()));
        Assert.False(body["monitored"]!.GetValue<bool>());
    }

    /// <summary>The performer flag flip is the same body against the performer's own id array.</summary>
    [Fact]
    public void ThePerformerFlagFlipCarriesTheIdArrayAndTheFlagAndNothingElse()
    {
        var body = V3BodyProjector.SetPerformerMonitored(11, monitored: false);

        Assert.Equal(
            ["monitored", "performerIds"],
            body.Select(member => member.Key).Order());
        Assert.Equal(
            [11],
            Assert.IsType<JsonArray>(body["performerIds"]).Select(id => id!.GetValue<int>()));
        Assert.False(body["monitored"]!.GetValue<bool>());
    }

    /// <summary>
    /// No flag flip in either direction, for either kind, carries a field a user may have changed
    /// inside Whisparr.
    /// </summary>
    /// <remarks>
    /// Asserted as member ABSENCE. An omitted nullable field is not applied and a field sent as null
    /// is, so the two cannot be told apart by a value.
    /// </remarks>
    [Fact]
    public void NoFlagFlipCarriesAFieldTheUserOwns()
    {
        JsonObject[] flips =
        [
            V3BodyProjector.SetStudioMonitored(4, monitored: true),
            V3BodyProjector.SetStudioMonitored(4, monitored: false),
            V3BodyProjector.SetPerformerMonitored(11, monitored: true),
            V3BodyProjector.SetPerformerMonitored(11, monitored: false),
        ];

        Assert.All(
            flips,
            body => Assert.All(
                FieldsAFlagFlipMustNotCarry, field => Assert.False(body.ContainsKey(field))));
    }

    /// <summary>
    /// Asking for the flag an entity already has composes the same body and is not an error.
    /// </summary>
    /// <remarks>
    /// Byte-identical rather than equivalent: a body carrying a value derived from the moment it was
    /// composed would differ between two composes of the same request.
    /// </remarks>
    [Fact]
    public void FlippingTheSameFlagTwiceComposesByteIdenticalBodies()
    {
        Assert.Equal(
            V3BodyProjector.SetStudioMonitored(4, monitored: true).ToJsonString(),
            V3BodyProjector.SetStudioMonitored(4, monitored: true).ToJsonString());
        Assert.Equal(
            V3BodyProjector.SetPerformerMonitored(11, monitored: true).ToJsonString(),
            V3BodyProjector.SetPerformerMonitored(11, monitored: true).ToJsonString());
    }

    /// <summary>
    /// The add-time gate is a present member for the narrower scope and an absent one for the wider.
    /// </summary>
    /// <remarks>
    /// The gate's own help text says an empty value is ignored, so omission is how the wider scope is
    /// expressed and there is no value that expresses it.
    /// </remarks>
    [Fact]
    public void TheAddTimeGateIsPresentForTheNarrowerScopeAndAbsentForTheWider()
    {
        Assert.Equal(
            "2026-09-02T00:00:00Z",
            V3BodyProjector
                .AddStudio(StudioForeignId, MonitorScope.FutureScenes, Defaults, Now)["afterDate"]
                ?.GetValue<string>());
        Assert.False(
            V3BodyProjector.AddStudio(StudioForeignId, MonitorScope.AllScenes, Defaults, Now)
                .ContainsKey("afterDate"));
    }

    /// <summary>
    /// A scope change is composed on the whole resource as read, keeping every value the user chose.
    /// </summary>
    /// <remarks>
    /// The editor resource declares no add-time gate, so a scope sent there is accepted and applies
    /// nothing. This is the one verb that reads before it writes.
    /// </remarks>
    [Fact]
    public void AScopeChangeIsComposedOnTheWholeResourceAndKeepsWhatTheUserChose()
    {
        var held = Parse(HeldStudio);

        var narrowed = V3BodyProjector.WithScope(held, MonitorScope.FutureScenes, Now);
        var widened = V3BodyProjector.WithScope(held, MonitorScope.AllScenes, Now);

        Assert.Equal("2026-09-02T00:00:00Z", narrowed["afterDate"]!.GetValue<string>());
        Assert.False(widened.ContainsKey("afterDate"));

        foreach (var body in new[] { narrowed, widened })
        {
            Assert.Equal(4, body["id"]!.GetValue<int>());
            Assert.Equal(4, body["qualityProfileId"]!.GetValue<int>());
            Assert.Equal("/config/library", body["rootFolderPath"]!.GetValue<string>());
            Assert.Equal([7], Assert.IsType<JsonArray>(body["tags"]).Select(tag => tag!.GetValue<int>()));
        }

        // The resource read in is not mutated, so a caller can compose both scopes from one read.
        Assert.Equal("2026-09-02", held["afterDate"]!.GetValue<string>());
    }

    /// <summary>An entity whose catalogue is empty composes a scope body like any other.</summary>
    /// <remarks>
    /// A freshly added studio reads a catalogue of zero before its first refresh, so no scope path may
    /// require a catalogue to exist.
    /// </remarks>
    [Fact]
    public void AnEntityWithAnEmptyCatalogueStillComposesAValidScopeBody()
    {
        var fresh = Parse(EmptyCatalogueStudio);

        var narrowed = V3BodyProjector.WithScope(fresh, MonitorScope.FutureScenes, Now);

        Assert.Equal(0, narrowed["totalSceneCount"]!.GetValue<int>());
        Assert.Equal("2026-09-02T00:00:00Z", narrowed["afterDate"]!.GetValue<string>());
        Assert.Equal(9, narrowed["id"]!.GetValue<int>());
        Assert.False(V3BodyProjector.WithScope(fresh, MonitorScope.AllScenes, Now).ContainsKey("afterDate"));
    }

    /// <summary>A scope value this product does not express resolves to no scope at all.</summary>
    /// <remarks>
    /// Both scope-taking paths, because the one that resolved by default would be the one that marks a
    /// whole back catalogue wanted.
    /// </remarks>
    [Fact]
    public void AnUnrecognisedScopeThrowsRatherThanResolvingToAScope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V3BodyProjector.AddStudio(StudioForeignId, (MonitorScope)(-1), Defaults, Now));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V3BodyProjector.WithScope(Parse(HeldStudio), (MonitorScope)(-1), Now));
    }

    /// <summary>
    /// Every add this product can compose carries both acquisition-suppressing spellings, each present
    /// as a member and each false.
    /// </summary>
    /// <remarks>
    /// Presence is asserted apart from the value. An absent member and a false one read the same off a
    /// deserialized object, and the instance's default for the absent case is what this product must
    /// never depend on.
    /// </remarks>
    [Fact]
    public void EveryComposedAddCarriesBothSuppressionSpellingsPresentAndFalse()
    {
        Assert.All(EveryAdd(), AssertSuppressedInBothSpellings);
        Assert.NotEmpty(EveryAdd());
    }

    /// <summary>
    /// Every add carries the two columns the instance's database requires and a usable profile.
    /// </summary>
    /// <remarks>
    /// Both columns are NOT NULL with no rule set in front of them, so a missing value is answered
    /// with a raw database message rather than a validation failure.
    /// </remarks>
    [Fact]
    public void EveryComposedAddCarriesTheColumnsTheInstanceRequiresAndANonZeroProfile()
        => Assert.All(
            EveryAdd(),
            body =>
            {
                Assert.Equal("/config/library", body["rootFolderPath"]!.GetValue<string>());
                Assert.True(body.ContainsKey("tags"));
                Assert.IsType<JsonArray>(body["tags"]);
                Assert.True(body["qualityProfileId"]!.GetValue<int>() > 0);
                Assert.True(body["monitored"]!.GetValue<bool>());
            });

    /// <summary>An add composed with a profile the instance would accept and never act on.</summary>
    /// <remarks>
    /// This generation accepts a zero profile id, echoes it back, and the entity then monitors happily
    /// and can never acquire anything. The stop that reads the instance's offered list is the first
    /// guard; this is the last one before the request is composed.
    /// </remarks>
    [Fact]
    public void AnAddComposedWithAProfileTheInstanceWouldNeverActOnIsRefused()
    {
        var unusable = new AddDefaults(0, "/config/library");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => V3BodyProjector.AddStudio(StudioForeignId, MonitorScope.AllScenes, unusable, Now));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V3BodyProjector.AddPerformer(PerformerForeignId, unusable));
    }

    /// <summary>
    /// The performer add expresses no scope, because the field a future-only scope is expressed
    /// through exists on the studio resource and on no other.
    /// </summary>
    /// <remarks>
    /// Asserted on the signature as well as on the body: a parameter that existed would be a promise
    /// no member could keep, whatever the body it composed.
    /// </remarks>
    [Fact]
    public void ThePerformerAddExpressesNoScopeAtAll()
    {
        var body = V3BodyProjector.AddPerformer(PerformerForeignId, Defaults);

        Assert.False(body.ContainsKey("afterDate"));
        Assert.Equal(PerformerForeignId, body["foreignId"]!.GetValue<string>());

        var add = typeof(V3BodyProjector).GetMethod(
            nameof(V3BodyProjector.AddPerformer), BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(add);
        Assert.DoesNotContain(
            add.GetParameters(), parameter => parameter.ParameterType == typeof(MonitorScope));
    }

    /// <summary>Every add body this product can compose, over every kind and every scope.</summary>
    private static IReadOnlyList<JsonObject> EveryAdd() =>
    [
        V3BodyProjector.AddStudio(StudioForeignId, MonitorScope.FutureScenes, Defaults, Now),
        V3BodyProjector.AddStudio(StudioForeignId, MonitorScope.AllScenes, Defaults, Now),
        V3BodyProjector.AddPerformer(PerformerForeignId, Defaults),
    ];

    private static void AssertSuppressedInBothSpellings(JsonObject body)
    {
        Assert.True(body.ContainsKey("searchOnAdd"));
        var addOptions = Assert.IsType<JsonObject>(body["addOptions"]);
        Assert.True(addOptions.ContainsKey("searchForMovie"));

        // Read together rather than checked one at a time, so a body carrying one spelling true and
        // the other false cannot satisfy two independent assertions.
        Assert.Equal(
            (false, false),
            (body["searchOnAdd"]!.GetValue<bool>(), addOptions["searchForMovie"]!.GetValue<bool>()));
    }

    private static JsonObject Parse(string body)
        => Assert.IsType<JsonObject>(JsonNode.Parse(body));
}
