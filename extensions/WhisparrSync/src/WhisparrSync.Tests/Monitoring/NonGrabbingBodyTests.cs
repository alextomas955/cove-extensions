using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// One combination of generation, entity kind and scope that composes an add body.
// SuppressionPaths holds every acquisition-suppressing member the resource this add names
// declares, transcribed from that resource's own schema. The three resources an add can name
// declare three different spellings.
internal sealed record ComposedAdd(
    WhisparrGeneration Generation,
    WhisparrEntityKind Kind,
    MonitorScope? Scope,
    JsonObject Body,
    IReadOnlyList<string> SuppressionPaths);

// Every add body this product can compose, enumerated from the capability table each generation
// is built with. The case list is derived from GenerationCapabilities.CapabilitiesOf rather than
// transcribed: the failure to catch is a generation-and-kind combination that becomes registered
// and is never covered, and a transcribed list would go on agreeing with itself while that
// combination composed whatever it liked.
// A registered capability with no case here throws, and the message says what to add.
internal static class ComposedAdds
{
    // Transcribed from the two generations' own interface bundles.
    public static readonly string[] GrabbingCommandNames =
    [
        "StudiosSearch",
        "PerformersSearch",
        "MoviesSearch",
        "SeriesSearch",
        "MissingMoviesSearch",
        "CutoffUnmetMoviesSearch",
        "MissingEpisodeSearch",
        "CutoffUnmetEpisodeSearch",
        "EpisodeSearch",
    ];

    // Subtracted from the mounted set below rather than added to it. A caller asserts the
    // subtraction has exactly one member, so a second grabbing route mounted later cannot be
    // absorbed into this list without that assertion failing first.
    public static readonly string[] GrabbingEntityVerbs = ["search-all-monitored"];

    // A scene the library holds that an instance's catalogue does not.
    private const string SceneForeignId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    // The one-entity path every entity verb hangs off, as the emitted document spells it.
    private const string EntityPathPrefix = "/entity/{kind}/{coveId}/";

    // Transcribed rather than derived from a registration. The enumeration below filters on this
    // rather than on whether a capability is registered: a grabbing capability leaking into a
    // non-grabbing case list would assert that a grab body is non-grabbing.
    public static readonly WhisparrCapability[] GrabbingCapabilities =
        [WhisparrCapability.SearchMonitored, WhisparrCapability.SearchScene];

    private static readonly AddDefaults Defaults = new(4, "/config/library");

    // For a generation that refuses a profile the other one accepts.
    private static readonly AddDefaults V2Defaults = new(1, "/config/library");

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    // Carries both acquisition-suppressing spellings set true, which is what an instance answers
    // with for a studio a person added in the instance's own interface with search-on-add ticked.
    // A body composed by cloning this one carries whatever it holds, so a fixture without them
    // could not fail an assertion about what such a body says.
    private const string HeldStudio = """
        {"id":4,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true,
         "afterDate":"2026-09-02","qualityProfileId":4,"rootFolderPath":"/config/library","tags":[7],
         "searchOnAdd":true,"addOptions":{"searchForMovie":true}}
        """;

    public static IReadOnlyList<WhisparrGeneration> Generations { get; } =
        [WhisparrGeneration.V3, WhisparrGeneration.V2];

    public static IReadOnlyList<ComposedAdd> On(WhisparrGeneration generation) =>
    [
        .. GenerationCapabilities.CapabilitiesOf(generation)
            .Where(capability => !GrabbingCapabilities.Contains(capability))
            .SelectMany(capability => CasesFor(generation, capability))
    ];

    public static IReadOnlyList<ComposedAdd> All() => [.. Generations.SelectMany(On)];

    // The refresh is composed on the scene-registration path, so it reaches the same command
    // route a search would and is searched for a grabbing command name alongside the adds.
    public static IReadOnlyList<JsonObject> EveryNonGrabbingBody() =>
    [
        .. All().Select(added => added.Body),
        ComposedBody.Of(V3BodyProjector.SetStudioMonitored(4, monitored: true)),
        ComposedBody.Of(V3BodyProjector.SetStudioMonitored(4, monitored: false)),
        ComposedBody.Of(V3BodyProjector.SetPerformerMonitored(11, monitored: true)),
        ComposedBody.Of(V3BodyProjector.SetPerformerMonitored(11, monitored: false)),
        ComposedBody.Of(V3BodyProjector.SceneMonitorPatch(monitored: true)),
        ComposedBody.Of(V3BodyProjector.SceneMonitorPatch(monitored: false)),
        ComposedBody.Of(V3BodyProjector.SceneExclusion(SceneForeignId)),
        .. EveryScopeChange(),
        V3BodyProjector.RefreshCatalogue(WhisparrEntityKind.Studio, 4),
        V3BodyProjector.RefreshCatalogue(WhisparrEntityKind.Performer, 11),
        V2BodyProjector.RefreshCatalogue(3),
    ];

    // The one verb this product composes by cloning a whole instance response rather than field
    // for field, so its body carries members this product never wrote.
    public static IReadOnlyList<JsonObject> EveryScopeChange() =>
    [
        V3BodyProjector.WithScope(Held(), MonitorScope.FutureScenes, Now),
        V3BodyProjector.WithScope(Held(), MonitorScope.AllScenes, Now),
    ];

    // Transcribed from build 3.4.0.1387's own resources. Neither the studio nor the performer
    // resource declares an add-options member, so a body carrying one sends a member the instance
    // discards.
    public const string TopLevelSuppression = "searchOnAdd";

    public const string SceneSuppression = "addOptions.searchForMovie";

    // Both, because that generation reads one for the back catalogue and one for the cutoff
    // sweep, and a body setting either alone leaves the other at the instance's own default.
    public static readonly string[] V2Suppression =
    [
        "addOptions.searchForMissingEpisodes",
        "addOptions.searchForCutoffUnmetEpisodes",
    ];

    // The set that lets an add be asserted against the spellings it does not declare: a body
    // carrying one of those is composed for a schema other than the one it is being sent to.
    public static readonly string[] EverySuppressionSpelling =
    [
        TopLevelSuppression,
        SceneSuppression,
        .. V2Suppression,
    ];

    // Absent and false read the same off a value, so a caller asserts on this being non-null
    // separately from asserting what it holds.
    public static JsonNode? At(JsonObject body, string path)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(path);

        JsonNode? node = body;
        foreach (var segment in path.Split('.'))
        {
            node = (node as JsonObject)?[segment];
        }

        return node;
    }

    private static IReadOnlyList<ComposedAdd> CasesFor(
        WhisparrGeneration generation, WhisparrCapability capability)
        => (generation, capability) switch
        {
            // Carries a secret off the instance's own address and composes no add of any kind.
            (_, WhisparrCapability.OutOfBandCallbackSecret) => [],

            // Reads what the instance holds for a scene through two gets and composes no body at
            // all, so it contributes no add to enumerate.
            (_, WhisparrCapability.ReadSceneStatus) => [],

            // Reads the instance's exclusion list through one get and composes no body at all.
            (_, WhisparrCapability.ReadSceneExclusions) => [],

            // Sets one flag on a scene the instance already holds and registers nothing, so it adds
            // no catalogue item to enumerate. Its own composed body is covered beside the flag flips.
            (_, WhisparrCapability.MonitorScene) => [],

            // Excludes a scene from what the instance would take and adds no catalogue item, so it
            // contributes no add either. Its own body is covered beside the flag flips.
            (_, WhisparrCapability.ExcludeScene) => [],

            // Reads which of a set of scenes one site already holds a row for, through one get, and
            // composes no body at all.
            (_, WhisparrCapability.ReadSiteSceneRows) => [],

            // Reads which of a set of sites the instance holds, through one get, and composes no
            // body at all.
            (_, WhisparrCapability.ReadHeldSites) => [],

            // Read a page of cards in one request. Each sends the identifiers asked about and
            // nothing else, so neither composes an add to enumerate.
            (_, WhisparrCapability.ReadEntityCardsInBatch) => [],

            (_, WhisparrCapability.ReadSceneCardsInBatch) => [],

            // Reads one entity's own scene list through one get and composes no body at all.
            (_, WhisparrCapability.ReadEntityCatalogue) => [],

            // Reads what the instance holds at a path of its own, through one get, and composes no
            // body at all.
            (_, WhisparrCapability.ReadInstanceFilesystem) => [],

            // One body per kind: the add is the same shape for both, and the route it goes to is
            // what differs.
            (WhisparrGeneration.V3, WhisparrCapability.TrackEntityCatalogue) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    Scope: null,
                    V3BodyProjector.TrackEntity(MonitorHost.StudioRemoteIdValue, Defaults),
                    [TopLevelSuppression]),
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Performer,
                    Scope: null,
                    V3BodyProjector.TrackEntity(MonitorHost.PerformerRemoteIdValue, Defaults),
                    [TopLevelSuppression]),
            ],

            // This generation tracks a site through the presence-only add it already composes.
            (WhisparrGeneration.V2, WhisparrCapability.TrackEntityCatalogue) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    Scope: null,
                    ComposedBody.Of(V2BodyProjector.RegisterSite(3, Defaults)),
                    V2Suppression),
            ],

            (WhisparrGeneration.V3, WhisparrCapability.MonitorStudio) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    MonitorScope.FutureScenes,
                    ComposedBody.Of(V3BodyProjector.AddStudio(
                        MonitorHost.StudioRemoteIdValue, MonitorScope.FutureScenes, Defaults, Now)),
                    [TopLevelSuppression]),
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    MonitorScope.AllScenes,
                    ComposedBody.Of(V3BodyProjector.AddStudio(
                        MonitorHost.StudioRemoteIdValue, MonitorScope.AllScenes, Defaults, Now)),
                    [TopLevelSuppression]),
            ],

            // One case and no scope: the field a future-only scope is expressed through is on the
            // studio resource and on no other, so this kind has no second combination to cover.
            (WhisparrGeneration.V3, WhisparrCapability.MonitorPerformer) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Performer,
                    null,
                    ComposedBody.Of(V3BodyProjector.AddPerformer(MonitorHost.PerformerRemoteIdValue, Defaults)),
                    [TopLevelSuppression]),
            ],

            // One catalogue item registered per scene, with the monitor type covering that scene
            // alone and the add method recording that a person asked for it.
            (WhisparrGeneration.V3, WhisparrCapability.RegisterMissingScenes) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    null,
                    ComposedBody.Of(V3BodyProjector.AddScene(SceneForeignId, Defaults)),
                    [SceneSuppression]),
            ],

            // Attaches files the library already holds and adds no catalogue item of any kind, so it
            // contributes no add body to enumerate. Registered on both generations, because the two
            // cases it decides between are identical on each.
            (_, WhisparrCapability.ReflectOwnedFiles) => [],

            // Whisparr v2 addresses a studio as a series, and its add is composed from the
            // numeric identifier its own lookup answered with rather than from the one the library
            // holds. The identifiers below are what that lookup was measured answering.
            (WhisparrGeneration.V2, WhisparrCapability.MonitorStudio) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    MonitorScope.FutureScenes,
                    ComposedV2Body.Of(V2BodyProjector.AddStudio(
                        3372, MonitorScope.FutureScenes, V2Defaults)),
                    V2Suppression),
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    MonitorScope.AllScenes,
                    ComposedV2Body.Of(V2BodyProjector.AddStudio(
                        3372, MonitorScope.AllScenes, V2Defaults)),
                    V2Suppression),
            ],

            // The presence-only add on the same generation, composed from the same resolved number
            // its lookup answers with. It carries the same two suppression flags as the monitoring
            // add and, unlike it, monitors nothing.
            (WhisparrGeneration.V2, WhisparrCapability.RegisterOwnedSites) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    null,
                    ComposedV2Body.Of(V2BodyProjector.RegisterSite(3372, V2Defaults)),
                    V2Suppression),
            ],

            _ => throw new NotSupportedException(
                $"{generation} holds {capability} and no composed add body for it is enumerated in "
                    + $"{nameof(ComposedAdds)}. Add the combination's body beside the others so the "
                    + "never-search assertions cover it."),
        };

    public static JsonObject Held()
        => (JsonObject)JsonNode.Parse(HeldStudio)!;

    // Read out of the emitted document rather than from a hand-written array, for the same reason
    // the case list above reads the capability table: the failure to catch is a route mounted
    // later and never driven through an assertion that it grabs nothing. The document is emitted
    // from the shipped registrations. It is the same document the browser's own route pin reads.
    public static IReadOnlyList<string> MountedEntityVerbs()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(WireDocument.Path()));

        return
        [
            .. document.RootElement.GetProperty("paths").EnumerateObject()
                .Where(path => path.Value.TryGetProperty("post", out _))
                .Where(path => path.Name.Contains(EntityPathPrefix, StringComparison.Ordinal))
                .Select(path => path.Name[
                    (path.Name.IndexOf(EntityPathPrefix, StringComparison.Ordinal)
                        + EntityPathPrefix.Length)..])
                .Where(verb => !verb.Contains('/', StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
    }

    // The scene-segment placeholder every per-scene route names its scene in.
    private const string SceneSegment = "{providerSceneId}";

    // A second enumeration because these hang off a scene segment rather than off the entity, so
    // they never appear in the one-segment set above. Read out of the emitted document for the
    // same reason that one is: a per-scene route mounted later is covered without an edit.
    public static IReadOnlyList<string> MountedSceneVerbs()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(WireDocument.Path()));

        return
        [
            .. document.RootElement.GetProperty("paths").EnumerateObject()
                .Where(path => path.Value.TryGetProperty("post", out _))
                .Where(path => path.Name.Contains(SceneSegment, StringComparison.Ordinal))
                .Select(path => path.Name[
                    (path.Name.IndexOf(SceneSegment, StringComparison.Ordinal)
                        + SceneSegment.Length + 1)..])
                .Order(StringComparer.Ordinal)
        ];
    }

    // Subtracted from the mounted per-scene set, with the subtraction asserted to have exactly
    // one member.
    public static readonly string[] GrabbingSceneVerbs = ["search"];
}

// The behavioural half of the never-search guarantee: what every composed body actually says,
// over every combination the registered capabilities allow. The type-level half is asserted in
// the invariant group. Nothing here reads a status or a count.
// The guarantee is not that no grabbing verb is reachable. It is that exactly one named gesture
// reaches exactly one, and that every other mounted verb reaches none. The second half is driven
// over the mounted set read from the emitted document, so a verb mounted later is covered without
// an edit.
public sealed class NonGrabbingBodyTests
{
    // The counts measure how much of the never-search guarantee is behaviourally covered. They are
    // asserted exactly, so a monitoring capability registered for either generation makes this
    // false whether or not its case was added, and a case added without a registration does too.
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void TheCaseListIsDerivedFromTheRegisteredCapabilityTable()
    {
        Assert.Equal(6, ComposedAdds.On(WhisparrGeneration.V3).Count);
        Assert.Equal(
            [
                WhisparrCapability.OutOfBandCallbackSecret,
                WhisparrCapability.MonitorStudio,
                WhisparrCapability.MonitorPerformer,
                WhisparrCapability.RegisterMissingScenes,
                WhisparrCapability.ReflectOwnedFiles,
                WhisparrCapability.SearchMonitored,
                WhisparrCapability.ReadSceneStatus,
                WhisparrCapability.ReadSceneExclusions,
                WhisparrCapability.SearchScene,
                WhisparrCapability.MonitorScene,
                WhisparrCapability.ExcludeScene,
                WhisparrCapability.ReadEntityCardsInBatch,
                WhisparrCapability.ReadSceneCardsInBatch,
                WhisparrCapability.TrackEntityCatalogue,
                WhisparrCapability.ReadEntityCatalogue,
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V3));

        Assert.Equal(4, ComposedAdds.On(WhisparrGeneration.V2).Count);
        Assert.Equal(
            [
                WhisparrCapability.OutOfBandCallbackSecret,
                WhisparrCapability.MonitorStudio,
                WhisparrCapability.ReflectOwnedFiles,
                WhisparrCapability.SearchMonitored,
                WhisparrCapability.MonitorScene,
                WhisparrCapability.RegisterOwnedSites,
                WhisparrCapability.ReadSiteSceneRows,
                WhisparrCapability.ReadHeldSites,
                WhisparrCapability.ReadEntityCardsInBatch,
                WhisparrCapability.TrackEntityCatalogue,
                WhisparrCapability.ReadEntityCatalogue,
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));

        Assert.Equal(10, ComposedAdds.All().Count);

        // The filter is on the verb class rather than on the registration, so a grabbing capability a
        // generation holds contributes no case to a list of bodies asserted non-grabbing. Which
        // generations hold each one is written out, because the two differ: the entity search is
        // registered on both and the per-scene search on v3 alone.
        Assert.All(
            ComposedAdds.Generations,
            generation => Assert.Contains(
                WhisparrCapability.SearchMonitored,
                GenerationCapabilities.CapabilitiesOf(generation)));
        Assert.Contains(
            WhisparrCapability.SearchScene,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V3));
        Assert.DoesNotContain(
            WhisparrCapability.SearchScene,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));
    }

    // The three resources an add can name declare three different spellings, and v2 declares two
    // more. A body carrying one its resource does not declare is composed for a schema other than
    // the one it is being sent to: the instance discards it, so this product would be reading a
    // suppression it never applied.
    [Fact]
    public void NoAddCarriesASuppressionSpellingItsResourceDoesNotDeclare()
    {
        var composed = ComposedAdds.All();

        Assert.NotEmpty(composed);
        Assert.All(
            composed,
            added => Assert.All(
                ComposedAdds.EverySuppressionSpelling.Except(added.SuppressionPaths),
                undeclared => Assert.DoesNotContain(
                    undeclared.Split('.')[^1],
                    added.Body.ToJsonString(),
                    StringComparison.Ordinal)));
    }

    // Presence is asserted apart from the value. Omission happened to be safe on the builds this
    // was measured against, which is a property of those builds rather than of the contract.
    [Fact]
    public void EveryEnumeratedAddCarriesItsResourcesSuppressionSpellingsPresentAndFalse()
        => Assert.All(
            ComposedAdds.All(),
            added =>
            {
                Assert.NotEmpty(added.SuppressionPaths);
                Assert.All(
                    added.SuppressionPaths,
                    path => Assert.NotNull(ComposedAdds.At(added.Body, path)));
                Assert.All(
                    added.SuppressionPaths,
                    path => Assert.False(ComposedAdds.At(added.Body, path)!.GetValue<bool>()));
            });

    // Every spelling is read into one comparison rather than checked one at a time, because two
    // independent assertions are each satisfiable by a body the other one would refuse.
    [Fact]
    public void NoEnumeratedAddCarriesOneSpellingTrueAndAnotherFalse()
        => Assert.All(
            ComposedAdds.All(),
            added => Assert.Equal(
                added.SuppressionPaths.Select(_ => (bool?)false).ToArray(),
                added.SuppressionPaths
                    .Select(path => ComposedAdds.At(added.Body, path)?.GetValue<bool>())
                    .ToArray()));

    // Searched as serialised text rather than at a known member, so a name nested at any depth is
    // caught. A composed command body is not the only way a name could arrive.
    [Fact]
    public void NoNonGrabbingBodyNamesAGrabbingCommand()
    {
        var bodies = ComposedAdds.EveryNonGrabbingBody();

        Assert.NotEmpty(bodies);
        Assert.NotEmpty(ComposedAdds.GrabbingCommandNames);
        Assert.All(
            bodies,
            body => Assert.All(
                ComposedAdds.GrabbingCommandNames,
                name => Assert.DoesNotContain(name, body.ToJsonString(), StringComparison.Ordinal)));
    }

    // Absence and false are both admissible here, because the flag flips and the catalogue
    // refreshes carry no such member at all: what is refused is a member present and true. The
    // adds are asserted present-and-false separately.
    // Both generations' spellings are read over every body. A body composed by cloning what an
    // instance answered carries whatever that instance holds, so the flag reaching a request is
    // not decided by which generation's projector composed it.
    [Fact]
    public void NoNonGrabbingBodyCarriesASuppressionSpellingSetTrue()
    {
        var bodies = ComposedAdds.EveryNonGrabbingBody();
        var paths = ComposedAdds.EverySuppressionSpelling;

        Assert.NotEmpty(bodies);
        Assert.NotEmpty(paths);
        Assert.All(
            bodies,
            body => Assert.All(
                paths,
                path => Assert.NotEqual<bool?>(
                    true, ComposedAdds.At(body, path)?.GetValue<bool>())));
    }

    // The resource is what the instance answered a read with, so it carries the user's own flags:
    // an entity added in the instance's interface with search-on-add ticked holds them true, and a
    // clone that left them alone would re-assert them on a request this product originated.
    // What the resource carried is asserted first, so a fixture quietly losing either spelling
    // fails here rather than making the rest vacuous. Overwritten rather than removed, and
    // presence is asserted apart from the value: removal would rest on the instance defaulting an
    // absent member to false, which was never measured.
    [Fact]
    public void AScopeChangeOverwritesBothSuppressionSpellingsOnWhatTheInstanceHeld()
    {
        // The two v3's resources declare between them, which is what a resource
        // this product clones back out can be carrying.
        string[] paths = [ComposedAdds.TopLevelSuppression, ComposedAdds.SceneSuppression];
        var scopeChanges = ComposedAdds.EveryScopeChange();

        // The resource composed over holds both spellings true, which is the case a body carrying
        // neither cannot be asserted against.
        Assert.All(
            paths,
            path => Assert.True(ComposedAdds.At(ComposedAdds.Held(), path)!.GetValue<bool>()));

        Assert.NotEmpty(scopeChanges);
        Assert.All(
            scopeChanges,
            body =>
            {
                Assert.All(paths, path => Assert.NotNull(ComposedAdds.At(body, path)));
                Assert.All(
                    paths, path => Assert.False(ComposedAdds.At(body, path)!.GetValue<bool>()));
            });
    }

    // The member set is asserted rather than a spelling's absence, so a field the instance holds
    // for that scene cannot ride out on this request whatever it is called. Read off the composed
    // body, so no search flag is absent by an omission default nobody measured.
    [Fact]
    public void TheSceneMonitorBodyCarriesTheFlagAndNoOtherMember()
    {
        foreach (var monitored in new[] { true, false })
        {
            var body = ComposedBody.Of(V3BodyProjector.SceneMonitorPatch(monitored));

            Assert.Equal(["monitored"], body.Select(member => member.Key).ToList());
            Assert.Equal(monitored, body["monitored"]!.GetValue<bool>());
            Assert.All(
                ComposedAdds.EverySuppressionSpelling,
                path => Assert.Null(ComposedAdds.At(body, path)));
        }
    }

    // The instance validates the title as non-empty and binds it as movieTitle, so the spelling is
    // asserted as well as the presence: a body naming the identifier alone, or naming a plain
    // title, is refused and nothing is excluded. The instance's own exclusion form always carries
    // a type. The member set is asserted too, so the reason stays the instance's own to fill.
    [Fact]
    public void TheSceneExclusionBodyCarriesTheForeignIdAMovieTitleAndTheSceneType()
    {
        var body = ComposedBody.Of(V3BodyProjector.SceneExclusion("9b6a0f8e-5f2c-4a1d-8b7e-2c3d4e5f6a7b"));

        Assert.Equal(
            ["foreignId", "movieTitle", "type"], body.Select(member => member.Key).Order().ToList());
        Assert.Equal(
            "9b6a0f8e-5f2c-4a1d-8b7e-2c3d4e5f6a7b", body["foreignId"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(body["movieTitle"]!.GetValue<string>()));
        Assert.Contains(
            "9b6a0f8e-5f2c-4a1d-8b7e-2c3d4e5f6a7b", body["movieTitle"]!.GetValue<string>());
        Assert.Equal("scene", body["type"]!.GetValue<string>());
        Assert.All(
            ComposedAdds.EverySuppressionSpelling,
            path => Assert.Null(ComposedAdds.At(body, path)));
    }

    // Every index rather than the last: a grab issued before the add would be just as acquiring,
    // and an assertion reading only the final entry would not see it. The class of each recorded
    // verb is read out of the transcribed table by indexer, so a verb nobody wrote down fails here
    // too.
    // Paired with an assertion that the log holds an acting verb, so a gesture that reached the
    // instance not at all cannot satisfy this.
    [Fact]
    public async Task AWholeMonitorGestureOnEachKindHoldsNoGrabbingVerbAtAnyPosition()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var performerId = await host.SeedPerformerAsync(
            MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);

        Assert.Equal(
            MonitorRefusalKind.None, (await host.MonitorAsync("studio", studioId)).Refusal);
        Assert.Equal(
            MonitorRefusalKind.None, (await host.MonitorAsync("performer", performerId)).Refusal);

        Assert.Contains(
            host.Client.Verbs,
            verb => Invariants.OutboundSeam.VerbClassByMember[verb] == WhisparrVerbClass.Act);
        Assert.All(
            host.Client.Verbs,
            verb => Assert.NotEqual(
                WhisparrVerbClass.Grab, Invariants.OutboundSeam.VerbClassByMember[verb]));
    }

    // Every index of each ordered verb log rather than its last entry: a grab issued before an act
    // would be just as acquiring, and an assertion reading only the final entry would not see it.
    // The mounted set comes from the emitted document rather than from a list written here, so a
    // verb mounted later is covered without an edit. The subtraction is asserted to have exactly
    // one member, so it cannot quietly grow to cover a second grabbing route.
    // Each verb is driven on its own host and its log is asserted non-empty, so a verb that
    // reached the instance not at all cannot satisfy this.
    [Fact]
    public async Task EveryMountedEntityVerbButTheSearchReachesNoGrabbingVerb()
    {
        var mounted = ComposedAdds.MountedEntityVerbs();
        var nonGrabbing = mounted
            .Except(ComposedAdds.GrabbingEntityVerbs, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(mounted);
        Assert.Equal(
            ComposedAdds.GrabbingEntityVerbs,
            mounted.Except(nonGrabbing, StringComparer.Ordinal));
        Assert.Single(mounted.Except(nonGrabbing, StringComparer.Ordinal));

        List<string> recorded = [];
        foreach (var verb in nonGrabbing)
        {
            await using var host = await MonitorHost.CreateAsync();
            host.Client.Answering(nameof(RecordingWhisparrCore.SetStudioScopeAsync), MonitorHost.Json(200, "{}"));
            host.Client.Answering(nameof(RecordingWhisparrCore.ListImportableFilesAsync), MonitorHost.Json(200, "{}"));
            host.Client.Answering(nameof(RecordingWhisparrCore.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
            host.Client
                .Answering(
                    nameof(IWhisparrStudioActing.ReadStudioAsync),
                    MonitorHost.Json(200, """{"id":9,"monitored":false}"""))
                .Answering(
                    nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                    MonitorHost.Json(200, """{"copyUsingHardlinks":true}"""));

            var studioId = await host.SeedStudioAsync(
                MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
            await host.SeedStudioFileAsync(studioId, "/library/vixen/2026");

            var answered = await host.PostRawAsync(
                "studio", studioId, verb, """{"scope":"futureScenes"}""");
            Assert.True(answered.IsSuccessStatusCode, verb);

            // Driven to completion: a verb whose work is enqueued has issued nothing yet, and the
            // requests that would grab are the ones the run makes rather than the ones the route
            // does.
            if (host.Jobs.Enqueued.Count > 0)
            {
                await host.Jobs.RunLastAsync(new RecordingJobProgress(), TestCt);
            }

            Assert.NotEmpty(host.Client.Verbs);
            Assert.All(
                host.Client.Verbs,
                sent => Assert.NotEqual(
                    WhisparrVerbClass.Grab, Invariants.OutboundSeam.VerbClassByMember[sent]));
            recorded.AddRange(host.Client.Verbs);
        }

        Assert.Contains(
            recorded, sent => Invariants.OutboundSeam.VerbClassByMember[sent] == WhisparrVerbClass.Act);
    }

    // The enumeration above excludes the grabbing capabilities from the bodies it asserts
    // non-grabbing. An empty exclusion list would make that filter a no-op and every Assert.All
    // over it vacuous. Each member is also asserted really held, so a capability nothing registers
    // cannot stand in for a real one.
    [Fact]
    public void TheGrabbingCapabilityFilterIsNonEmptyAndEveryMemberIsReallyHeld()
    {
        Assert.NotEmpty(ComposedAdds.GrabbingCapabilities);
        Assert.NotEmpty(ComposedAdds.GrabbingEntityVerbs);

        Assert.All(
            ComposedAdds.GrabbingCapabilities,
            grabbing => Assert.Contains(
                ComposedAdds.Generations,
                generation => GenerationCapabilities.CapabilitiesOf(generation).Contains(grabbing)));
    }

    // Both instances declare the role, so absence is not what keeps a monitoring path from
    // grabbing. What does is that the member lives on that role alone and no monitoring path asks
    // for it, which the whole-gesture case above asserts behaviourally.
    [Fact]
    public void TheGrabbingRoleIsReachedOnlyByAskingForItByName()
    {
        Assert.All(
            new[] { typeof(WhisparrV3Instance), typeof(WhisparrV2Instance) },
            instance => Assert.Contains(typeof(IWhisparrSearchGrabbing), instance.GetInterfaces()));

        Assert.Empty(GenerationCapabilities.CapabilitiesOf((WhisparrGeneration)(-1)));
    }

    // The same rule as the entity-verb case above, over the per-scene routes.
    [Fact]
    public async Task EveryMountedSceneVerbButTheSearchReachesNoGrabbingVerb()
    {
        const string sceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

        var mounted = ComposedAdds.MountedSceneVerbs();
        var nonGrabbing = mounted
            .Except(ComposedAdds.GrabbingSceneVerbs, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(mounted);
        Assert.Equal(
            ComposedAdds.GrabbingSceneVerbs,
            mounted.Except(nonGrabbing, StringComparer.Ordinal));
        Assert.Single(mounted.Except(nonGrabbing, StringComparer.Ordinal));

        List<string> recorded = [];
        foreach (var verb in nonGrabbing)
        {
            await using var host = await MonitorHost.CreateAsync();
            host.Client.Answering(nameof(RecordingWhisparrCore.AddSceneAsync), MonitorHost.Json(200, "{}"));
            var studioId = await host.SeedStudioAsync(
                MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

            var answered = await host.PostRawAsync(
                "studio", studioId, $"missing/{sceneId}/{verb}", "{}");
            Assert.True(answered.IsSuccessStatusCode, verb);

            Assert.NotEmpty(host.Client.Verbs);
            Assert.All(
                host.Client.Verbs,
                sent => Assert.NotEqual(
                    WhisparrVerbClass.Grab, Invariants.OutboundSeam.VerbClassByMember[sent]));
            recorded.AddRange(host.Client.Verbs);
        }

        Assert.Contains(
            recorded, sent => Invariants.OutboundSeam.VerbClassByMember[sent] == WhisparrVerbClass.Act);
    }
}
