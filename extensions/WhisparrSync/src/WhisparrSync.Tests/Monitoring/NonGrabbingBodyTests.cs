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

// Every add body this product can compose. The case list is derived from
// GenerationCapabilities.CapabilitiesOf, so a combination registered later is covered rather than
// missed; one with no case throws and the message says what to add. The name and path lists below
// are transcribed by hand instead, because a list read out of the code it checks would agree with
// that code whatever it said.
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

    // Subtracted from the mounted set below, and a caller asserts the subtraction has exactly one
    // member, so a second grabbing route cannot be absorbed here unnoticed.
    public static readonly string[] GrabbingEntityVerbs = ["search-all-monitored"];

    // A scene the library holds that an instance's catalogue does not.
    private const string SceneForeignId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    // The one-entity path every entity verb hangs off, as the emitted document spells it.
    private const string EntityPathPrefix = "/entity/{kind}/{coveId}/";

    // The enumeration filters on this rather than on whether a capability is registered: a grabbing
    // capability leaking into a non-grabbing case list would assert that a grab body is
    // non-grabbing.
    public static readonly WhisparrCapability[] GrabbingCapabilities =
        [WhisparrCapability.SearchMonitored, WhisparrCapability.SearchScene];

    private static readonly AddDefaults Defaults = new(4, "/config/library");

    // For a generation that refuses a profile the other one accepts.
    private static readonly AddDefaults V2Defaults = new(1, "/config/library");

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    // Both acquisition-suppressing spellings set true, as an instance answers for a studio added in
    // its own interface with search-on-add ticked. A scope change clones this body, so a fixture
    // without them could not fail an assertion about what such a body says.
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
            // No catalogue item, so no add to enumerate. The scene monitor and the exclusion do
            // compose a body; both are covered beside the flag flips above.
            (_, WhisparrCapability.OutOfBandCallbackSecret) => [],
            (_, WhisparrCapability.ReadSceneStatus) => [],
            (_, WhisparrCapability.ReadSceneExclusions) => [],
            (_, WhisparrCapability.MonitorScene) => [],
            (_, WhisparrCapability.ExcludeScene) => [],
            (_, WhisparrCapability.ReadSiteSceneRows) => [],
            (_, WhisparrCapability.ReadHeldSites) => [],
            (_, WhisparrCapability.ReadEntityCardsInBatch) => [],
            (_, WhisparrCapability.ReadSceneCardsInBatch) => [],
            (_, WhisparrCapability.ReadEntityCatalogue) => [],
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

            // Attaches files the library already holds and adds no catalogue item.
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

    // Read out of the emitted document, so a route mounted later is covered by the never-search
    // assertions without an edit. The document comes from the shipped registrations.
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

    // A second enumeration: these hang off a scene segment rather than off the entity, so they
    // never appear in the one-segment set above.
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

    // Subtracted from the mounted per-scene set, which a caller asserts has exactly one member.
    public static readonly string[] GrabbingSceneVerbs = ["search"];
}

// The behavioural half of the never-search guarantee, over every combination the registered
// capabilities allow; the type-level half is in the invariant group. The guarantee is not that no
// grabbing verb is reachable. It is that exactly one named gesture reaches exactly one, and every
// other mounted verb reaches none.
public sealed class NonGrabbingBodyTests
{
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The counts are asserted exactly, so a monitoring capability registered for either generation
    // fails this whether or not its case was added, and a case added without a registration does
    // too.
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

        // The filter is on the verb class rather than on the registration, so a grabbing capability
        // contributes no case to a list of bodies asserted non-grabbing. The two generations
        // differ: the entity search is registered on both and the per-scene search on v3 alone.
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

    // A body carrying a spelling its resource does not declare is composed for another schema. The
    // instance discards it, so this product would be reading a suppression it never applied.
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

    // Absence and false are both admissible: the flag flips and the refreshes carry no such member
    // at all, and what is refused is one present and true. The adds are asserted present-and-false
    // separately. Both generations' spellings are read over every body, because a body cloned from
    // what an instance answered carries whatever that instance holds.
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

    // The resource carries the user's own flags, so a clone that left them alone would re-assert
    // them on a request this product originated. What the resource carried is asserted first, so a
    // fixture losing either spelling fails here rather than making the rest vacuous. Overwritten
    // rather than removed: removal would rest on the instance defaulting an absent member to
    // false, which was never measured.
    [Fact]
    public void AScopeChangeOverwritesBothSuppressionSpellingsOnWhatTheInstanceHeld()
    {
        // What a v3 resource cloned back out can be carrying.
        string[] paths = [ComposedAdds.TopLevelSuppression, ComposedAdds.SceneSuppression];
        var scopeChanges = ComposedAdds.EveryScopeChange();

        // The resource holds both spellings true, so overwriting is what is being asserted.
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

    // Every index rather than the last: a grab issued before the add would be just as acquiring.
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

    // Every index rather than the last, for the reason the whole-gesture case above gives. Each
    // verb is driven on its own host and its log asserted non-empty, so a verb that reached the
    // instance not at all cannot satisfy this.
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
            // requests that would grab are the run's rather than the route's.
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

    // An empty exclusion list would make the filter above a no-op and every Assert.All over it
    // vacuous. Each member is asserted really held, so a capability nothing registers cannot stand
    // in for a real one.
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
    // grabbing. The member lives on that role alone and no monitoring path asks for it.
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
