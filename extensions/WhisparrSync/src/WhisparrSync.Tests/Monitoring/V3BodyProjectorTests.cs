using System.Reflection;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// The editor bodies are asserted on their key set, not only on their values. Every other field of
// the v3 editor resource is nullable and an omitted one is not applied, so a key present by
// accident overwrites a value the user chose.
//
// Nothing here asserts what happens to a scene released exactly on the add-time boundary date. The
// instance classifies that date and no behaviour of this product depends on it.
public sealed class V3BodyProjectorTests
{
    private const string StudioForeignId = "44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e";

    private const string PerformerForeignId = "9f0d6f27-1f3a-4a5f-8b21-6b2d3a5f9c10";

    // Fields a flag flip leaves alone, because the user owns each of them.
    private static readonly string[] FieldsAFlagFlipMustNotCarry =
        ["qualityProfileId", "rootFolderPath", "tags", "afterDate", "searchOnAdd", "addOptions"];

    // A studio as the instance holds one. The search flag is true, as an instance answers for a
    // studio added in its own interface with search-on-add ticked. The v3 studio resource declares
    // no add-options member, so this is the whole of what a body cloned from a read can carry.
    private const string HeldStudio = """
        {"id":4,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","title":"1000 Facials",
         "monitored":true,"afterDate":"2026-09-02","qualityProfileId":4,
         "rootFolderPath":"/config/library","tags":[7],"searchOnAdd":true,
         "sceneCount":4,"totalSceneCount":22}
        """;

    // A studio as a freshly added one reads, before any catalogue refresh has run.
    private const string EmptyCatalogueStudio = """
        {"id":9,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","title":"Fresh",
         "monitored":true,"qualityProfileId":1,"rootFolderPath":"/config/library","tags":[],
         "sceneCount":0,"totalSceneCount":0}
        """;

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly AddDefaults Defaults = new(4, "/config/library");

    [Fact]
    public void TheStudioFlagFlipCarriesTheIdArrayAndTheFlagAndNothingElse()
    {
        var body = ComposedBody.Of(V3BodyProjector.SetStudioMonitored(4, monitored: false));

        Assert.Equal(
            ["monitored", "studioIds"],
            body.Select(member => member.Key).Order());
        Assert.Equal([4], Assert.IsType<JsonArray>(body["studioIds"]).Select(id => id!.GetValue<int>()));
        Assert.False(body["monitored"]!.GetValue<bool>());
    }

    [Fact]
    public void ThePerformerFlagFlipCarriesTheIdArrayAndTheFlagAndNothingElse()
    {
        var body = ComposedBody.Of(V3BodyProjector.SetPerformerMonitored(11, monitored: false));

        Assert.Equal(
            ["monitored", "performerIds"],
            body.Select(member => member.Key).Order());
        Assert.Equal(
            [11],
            Assert.IsType<JsonArray>(body["performerIds"]).Select(id => id!.GetValue<int>()));
        Assert.False(body["monitored"]!.GetValue<bool>());
    }

    // Asserted as member absence. An omitted nullable field is not applied and a field sent as null
    // is, so the two cannot be told apart by a value.
    [Fact]
    public void NoFlagFlipCarriesAFieldTheUserOwns()
    {
        JsonObject[] flips =
        [
            ComposedBody.Of(V3BodyProjector.SetStudioMonitored(4, monitored: true)),
            ComposedBody.Of(V3BodyProjector.SetStudioMonitored(4, monitored: false)),
            ComposedBody.Of(V3BodyProjector.SetPerformerMonitored(11, monitored: true)),
            ComposedBody.Of(V3BodyProjector.SetPerformerMonitored(11, monitored: false)),
        ];

        Assert.All(
            flips,
            body => Assert.All(
                FieldsAFlagFlipMustNotCarry, field => Assert.False(body.ContainsKey(field))));
    }

    // Byte-identical rather than equivalent: a body carrying a value derived from the moment it was
    // composed would differ between two composes of the same request.
    [Fact]
    public void FlippingTheSameFlagTwiceComposesByteIdenticalBodies()
    {
        Assert.Equal(
            ComposedBody.Of(V3BodyProjector.SetStudioMonitored(4, monitored: true)).ToJsonString(),
            ComposedBody.Of(V3BodyProjector.SetStudioMonitored(4, monitored: true)).ToJsonString());
        Assert.Equal(
            ComposedBody.Of(V3BodyProjector.SetPerformerMonitored(11, monitored: true)).ToJsonString(),
            ComposedBody.Of(V3BodyProjector.SetPerformerMonitored(11, monitored: true)).ToJsonString());
    }

    // The instance ignores an empty add-time gate, so omission is the only way to express the wider
    // scope.
    [Fact]
    public void TheAddTimeGateIsPresentForTheNarrowerScopeAndAbsentForTheWider()
    {
        Assert.Equal(
            "2026-09-02T00:00:00Z",
            ComposedBody
                .Of(V3BodyProjector.AddStudio(StudioForeignId, MonitorScope.FutureScenes, Defaults, Now))
                ["afterDate"]
                ?.GetValue<string>());
        Assert.False(
            ComposedBody.Of(V3BodyProjector.AddStudio(StudioForeignId, MonitorScope.AllScenes, Defaults, Now))
                .ContainsKey("afterDate"));
    }

    // The v3 editor resource declares no add-time gate, so a scope sent there is accepted and
    // applies nothing. A scope change reads the whole resource first and composes on that.
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

    // The clone is the whole request, so a search flag the user ticked would otherwise be
    // re-asserted on a change this product originated. It is overwritten rather than removed,
    // because the instance's default for an absent member was never measured. The v3 studio
    // resource declares no add-options member, so composing one would send an unmeasured shape.
    [Fact]
    public void AScopeChangeOverwritesTheSearchFlagAndComposesNoAddOptions()
    {
        var held = Parse(HeldStudio);

        Assert.True(held["searchOnAdd"]!.GetValue<bool>());
        Assert.False(held.ContainsKey("addOptions"));

        foreach (var scope in new[] { MonitorScope.FutureScenes, MonitorScope.AllScenes })
        {
            var body = V3BodyProjector.WithScope(held, scope, Now);

            Assert.True(body.ContainsKey("searchOnAdd"));
            Assert.False(body["searchOnAdd"]!.GetValue<bool>());
            Assert.False(body.ContainsKey("addOptions"));
            Assert.DoesNotContain("searchForMovie", body.ToJsonString(), StringComparison.Ordinal);
        }

        Assert.True(held["searchOnAdd"]!.GetValue<bool>());
    }

    // A freshly added studio reads a catalogue of zero before its first refresh, so no scope path
    // can require a catalogue to exist.
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

    // Both scope-taking paths are checked, because a path that fell back to a default would be the
    // one that marks a whole back catalogue wanted.
    [Fact]
    public void AnUnrecognisedScopeThrowsRatherThanResolvingToAScope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ComposedBody.Of(V3BodyProjector.AddStudio(StudioForeignId, (MonitorScope)(-1), Defaults, Now)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V3BodyProjector.WithScope(Parse(HeldStudio), (MonitorScope)(-1), Now));
    }

    // Presence is asserted apart from the value, because an absent member and a false one read the
    // same off a deserialized object. In v3 build 3.4.0.1387 the studio and performer resources
    // declare a top-level suppression flag and no add-options member, and the scene resource
    // declares the reverse.
    [Fact]
    public void EveryComposedAddCarriesTheSuppressionSpellingItsResourceDeclares()
    {
        Assert.All(EveryAdd(), AssertSuppressedWhereTheResourceDeclaresIt);
        Assert.NotEmpty(EveryAdd());
    }

    // Both columns are NOT NULL in the instance's database with no validation rule in front of
    // them, so a missing value is answered with a raw database message.
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

    // v3 accepts a zero profile id and echoes it back. The entity then monitors and can never
    // acquire anything, so composing refuses the value.
    [Fact]
    public void AnAddComposedWithAProfileTheInstanceWouldNeverActOnIsRefused()
    {
        var unusable = new AddDefaults(0, "/config/library");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ComposedBody.Of(V3BodyProjector.AddStudio(StudioForeignId, MonitorScope.AllScenes, unusable, Now)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ComposedBody.Of(V3BodyProjector.AddPerformer(PerformerForeignId, unusable)));
    }

    // The add-time gate a future-only scope needs exists on the v3 studio resource and on no
    // other, so the performer add takes no scope parameter and composes no gate.
    [Fact]
    public void ThePerformerAddExpressesNoScopeAtAll()
    {
        var body = ComposedBody.Of(V3BodyProjector.AddPerformer(PerformerForeignId, Defaults));

        Assert.False(body.ContainsKey("afterDate"));
        Assert.Equal(PerformerForeignId, body["foreignId"]!.GetValue<string>());

        var add = typeof(V3BodyProjector).GetMethod(
            nameof(V3BodyProjector.AddPerformer), BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(add);
        Assert.DoesNotContain(
            add.GetParameters(), parameter => parameter.ParameterType == typeof(MonitorScope));
    }

    private static IReadOnlyList<JsonObject> EveryAdd() =>
    [
        ComposedBody.Of(V3BodyProjector.AddStudio(StudioForeignId, MonitorScope.FutureScenes, Defaults, Now)),
        ComposedBody.Of(V3BodyProjector.AddStudio(StudioForeignId, MonitorScope.AllScenes, Defaults, Now)),
        ComposedBody.Of(V3BodyProjector.AddPerformer(PerformerForeignId, Defaults)),
    ];

    private static void AssertSuppressedWhereTheResourceDeclaresIt(JsonObject body)
    {
        Assert.True(body.ContainsKey("searchOnAdd"));
        Assert.False(body["searchOnAdd"]!.GetValue<bool>());

        // These v3 resources declare no add-options member, so one here would be discarded and the
        // suppression never applied.
        Assert.False(body.ContainsKey("addOptions"));
    }

    private static JsonObject Parse(string body)
        => Assert.IsType<JsonObject>(JsonNode.Parse(body));
}
