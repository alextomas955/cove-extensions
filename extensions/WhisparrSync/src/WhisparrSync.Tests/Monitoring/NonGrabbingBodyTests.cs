using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>One combination of generation, entity kind and scope that composes an add body.</summary>
/// <param name="Generation">Whose spellings the suppression flags are read under.</param>
/// <param name="Kind">Which kind of entity the add names.</param>
/// <param name="Scope">The scope asked for, or null on a kind that expresses none.</param>
/// <param name="Body">The body as composed.</param>
/// <param name="SuppressionPaths">
/// Every acquisition-suppressing member the resource this add names declares, transcribed from that
/// resource's own schema. The three resources an add can name declare three different spellings.
/// </param>
internal sealed record ComposedAdd(
    WhisparrGeneration Generation,
    WhisparrEntityKind Kind,
    MonitorScope? Scope,
    JsonObject Body,
    IReadOnlyList<string> SuppressionPaths);

/// <summary>
/// Every add body this product can compose, enumerated from the capability table each generation is
/// built with.
/// </summary>
/// <remarks>
/// The case list is DERIVED from <see cref="GenerationCapabilities.CapabilitiesOf"/> rather than
/// transcribed. Elsewhere in this suite a transcribed list is the stronger source, because it
/// disagrees with the code the moment the code changes and someone has to reconcile them. Here the
/// risk runs the other way: the failure to catch is a generation-and-kind combination that becomes
/// registered and is never covered, and a transcribed list would go on agreeing with itself while
/// that combination composed whatever it liked. Reading the same table the product acts through makes
/// an uncovered registration a failure instead.
/// <para>
/// A registered capability with no case here throws, and the message says what to add. A suite red
/// for that reason is reporting an owed case rather than a defect.
/// </para>
/// </remarks>
internal static class ComposedAdds
{
    /// <summary>
    /// Every command name that makes an instance acquire something, transcribed from the two
    /// generations' own interface bundles.
    /// </summary>
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

    /// <summary>
    /// The entity verbs that MAY reach a grabbing command, transcribed by name.
    /// </summary>
    /// <remarks>
    /// Subtracted from the mounted set below rather than added to it. A caller asserts the
    /// subtraction has exactly one member, so a second grabbing route mounted later cannot be
    /// absorbed into this list without the assertion that names the count failing first.
    /// </remarks>
    public static readonly string[] GrabbingEntityVerbs = ["search-all-monitored"];

    /// <summary>A scene the library holds that an instance's catalogue does not.</summary>
    private const string SceneForeignId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    /// <summary>The one-entity path every entity verb hangs off, as the emitted document spells it.</summary>
    private const string EntityPathPrefix = "/entity/{kind}/{coveId}/";

    /// <summary>
    /// Every capability whose verb can make an instance acquire something, transcribed rather than
    /// derived from a registration.
    /// </summary>
    /// <remarks>
    /// The enumeration below filters on this rather than on whether a capability is registered. A
    /// grabbing capability leaking into a non-grabbing case list would either fail spuriously or,
    /// worse, assert that a grab body is non-grabbing.
    /// </remarks>
    public static readonly WhisparrCapability[] GrabbingCapabilities =
        [WhisparrCapability.SearchMonitored, WhisparrCapability.SearchScene];

    /// <summary>The instance-side values every enumerated add is composed with.</summary>
    private static readonly AddDefaults Defaults = new(4, "/config/library");

    /// <summary>The same, for a generation that refuses a profile the other one accepts.</summary>
    private static readonly AddDefaults V2Defaults = new(1, "/config/library");

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A studio as an instance holds one, for the verbs that read before they write.</summary>
    /// <remarks>
    /// It carries both acquisition-suppressing spellings set TRUE, which is what an instance answers
    /// with for a studio a person added in the instance's own interface with search-on-add ticked. A
    /// body composed by cloning this one carries whatever it holds, so a fixture without them cannot
    /// fail an assertion about what such a body says.
    /// </remarks>
    private const string HeldStudio = """
        {"id":4,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true,
         "afterDate":"2026-09-02","qualityProfileId":4,"rootFolderPath":"/config/library","tags":[7],
         "searchOnAdd":true,"addOptions":{"searchForMovie":true}}
        """;

    /// <summary>Every generation this product can be connected to.</summary>
    public static IReadOnlyList<WhisparrGeneration> Generations { get; } =
        [WhisparrGeneration.V3, WhisparrGeneration.V2];

    /// <summary>Every add body <paramref name="generation"/>'s registered capabilities compose.</summary>
    public static IReadOnlyList<ComposedAdd> On(WhisparrGeneration generation) =>
    [
        .. GenerationCapabilities.CapabilitiesOf(generation)
            .Where(capability => !GrabbingCapabilities.Contains(capability))
            .SelectMany(capability => CasesFor(generation, capability))
    ];

    /// <summary>Every add body this product can compose, over every generation.</summary>
    public static IReadOnlyList<ComposedAdd> All() => [.. Generations.SelectMany(On)];

    /// <summary>
    /// Every body composed on a path that must not acquire: the adds, both flag flips, both scope
    /// changes and every catalogue refresh.
    /// </summary>
    /// <remarks>
    /// The refresh is composed on the scene-registration path, so it is searched for a grabbing
    /// command name alongside the adds: it reaches the same command route a search would.
    /// </remarks>
    public static IReadOnlyList<JsonObject> EveryNonGrabbingBody() =>
    [
        .. All().Select(added => added.Body),
        ComposedBody.Of(V3BodyProjector.SetStudioMonitored(4, monitored: true)),
        ComposedBody.Of(V3BodyProjector.SetStudioMonitored(4, monitored: false)),
        ComposedBody.Of(V3BodyProjector.SetPerformerMonitored(11, monitored: true)),
        ComposedBody.Of(V3BodyProjector.SetPerformerMonitored(11, monitored: false)),
        ComposedBody.Of(V3BodyProjector.SceneMonitorPatch(monitored: true)),
        ComposedBody.Of(V3BodyProjector.SceneMonitorPatch(monitored: false)),
        .. EveryScopeChange(),
        V3BodyProjector.RefreshCatalogue(WhisparrEntityKind.Studio, 4),
        V3BodyProjector.RefreshCatalogue(WhisparrEntityKind.Performer, 11),
        V2BodyProjector.RefreshCatalogue(3),
    ];

    /// <summary>Every scope change, over the resource an instance answered a read with.</summary>
    /// <remarks>
    /// The one verb this product composes by cloning a whole instance response rather than field for
    /// field, so it is the one whose body carries members this product never wrote.
    /// </remarks>
    public static IReadOnlyList<JsonObject> EveryScopeChange() =>
    [
        V3BodyProjector.WithScope(Held(), MonitorScope.FutureScenes, Now),
        V3BodyProjector.WithScope(Held(), MonitorScope.AllScenes, Now),
    ];

    /// <summary>The flag the studio and performer resources declare, and the only one they do.</summary>
    /// <remarks>
    /// Transcribed from build 3.4.0.1387's own resources. Neither declares an add-options member, so
    /// a body carrying one is sending a member the instance discards.
    /// </remarks>
    public const string TopLevelSuppression = "searchOnAdd";

    /// <summary>The flag the scene resource declares, and the only one it does.</summary>
    /// <inheritdoc cref="TopLevelSuppression" path="/remarks"/>
    public const string SceneSuppression = "addOptions.searchForMovie";

    /// <summary>The two flags the older generation's own add resource declares.</summary>
    /// <remarks>
    /// Both, because that generation reads one for the back catalogue and one for the cutoff sweep,
    /// and a body setting either alone leaves the other at the instance's own default.
    /// </remarks>
    public static readonly string[] V2Suppression =
    [
        "addOptions.searchForMissingEpisodes",
        "addOptions.searchForCutoffUnmetEpisodes",
    ];

    /// <summary>
    /// Every spelling either generation suppresses acquisition through, transcribed by hand.
    /// </summary>
    /// <remarks>
    /// The set is what lets an add be asserted against the spellings it does NOT declare: a body
    /// carrying one of those is composed for a schema other than the one it is being sent to.
    /// </remarks>
    public static readonly string[] EverySuppressionSpelling =
    [
        TopLevelSuppression,
        SceneSuppression,
        .. V2Suppression,
    ];

    /// <summary>The member <paramref name="path"/> names, or null when the body carries none.</summary>
    /// <remarks>
    /// Absent and false read the same off a value, so a caller asserts on this being non-null
    /// separately from asserting what it holds.
    /// </remarks>
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

            // The older generation addresses a studio as a series, and its add is composed from the
            // numeric identifier its own lookup answered with rather than from the one the library
            // holds. The identifiers below are what that lookup was measured answering.
            (WhisparrGeneration.V2, WhisparrCapability.MonitorStudio) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    MonitorScope.FutureScenes,
                    ComposedV2Body.Of(V2BodyProjector.AddStudio(
                        3372, "Vixen", "vixen", MonitorScope.FutureScenes, V2Defaults)),
                    V2Suppression),
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    MonitorScope.AllScenes,
                    ComposedV2Body.Of(V2BodyProjector.AddStudio(
                        3372, "Vixen", "vixen", MonitorScope.AllScenes, V2Defaults)),
                    V2Suppression),
            ],

            _ => throw new NotSupportedException(
                $"{generation} holds {capability} and no composed add body for it is enumerated in "
                    + $"{nameof(ComposedAdds)}. Add the combination's body beside the others so the "
                    + "never-search assertions cover it."),
        };

    /// <summary>The resource every scope change is composed over.</summary>
    public static JsonObject Held()
        => (JsonObject)JsonNode.Parse(HeldStudio)!;

    /// <summary>
    /// The verb of every one-entity route the shipped wire document declares a POST for.
    /// </summary>
    /// <remarks>
    /// Read out of the EMITTED document rather than from a hand-written array, and for the same
    /// reason the case list above reads the capability table: the failure to catch is a route mounted
    /// later and never driven through an assertion that it grabs nothing. A transcribed list would go
    /// on agreeing with itself while that route did whatever it liked. The document is emitted from
    /// the shipped registrations, so it cannot.
    /// <para>
    /// It is the same document the browser's own route pin reads, so the two surfaces and this
    /// assertion cannot come to disagree about which verbs this build mounts.
    /// </para>
    /// </remarks>
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

    /// <summary>The scene-segment placeholder every per-scene route names its scene in.</summary>
    private const string SceneSegment = "{providerSceneId}";

    /// <summary>
    /// The verb of every per-scene route the shipped wire document declares a POST for.
    /// </summary>
    /// <remarks>
    /// A second enumeration because these hang off a scene segment rather than off the entity, so
    /// they never appear in the one-segment set above. Read out of the emitted document for the same
    /// reason that one is: a per-scene route mounted later is covered without an edit.
    /// </remarks>
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

    /// <summary>The per-scene verb that MAY reach a grabbing command, transcribed by name.</summary>
    /// <inheritdoc cref="GrabbingEntityVerbs" path="/remarks"/>
    public static readonly string[] GrabbingSceneVerbs = ["search"];
}

/// <summary>
/// The behavioural half of the never-search guarantee: what every composed body actually says, over
/// every combination the registered capabilities allow.
/// </summary>
/// <remarks>
/// The type-level half — which members can add and which single member can grab — is asserted in the
/// invariant group. Nothing here reads a status or a count: the subject is the body that would reach
/// an instance and the order in which the requests would leave.
/// <para>
/// The guarantee is not that no grabbing verb is reachable. It is that exactly one named gesture
/// reaches exactly one, and that every OTHER mounted verb reaches none. The second half is driven
/// here over the mounted set read from the emitted document, so a verb mounted later is covered
/// without an edit.
/// </para>
/// </remarks>
public sealed class NonGrabbingBodyTests
{
    /// <summary>
    /// The case list comes from the capability table, and how many cases each generation contributes
    /// is written down beside what that generation's table holds.
    /// </summary>
    /// <remarks>
    /// The counts are the measure of how much of the never-search guarantee is behaviourally covered.
    /// They are asserted exactly, so a monitoring capability registered for either generation makes
    /// this false whether or not its case was added — and a case added without a registration makes it
    /// false too.
    /// </remarks>
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void TheCaseListIsDerivedFromTheRegisteredCapabilityTable()
    {
        Assert.Equal(4, ComposedAdds.On(WhisparrGeneration.V3).Count);
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
            ],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V3));

        Assert.Equal(2, ComposedAdds.On(WhisparrGeneration.V2).Count);
        Assert.Equal(
            [
                WhisparrCapability.OutOfBandCallbackSecret,
                WhisparrCapability.MonitorStudio,
                WhisparrCapability.ReflectOwnedFiles,
                WhisparrCapability.SearchMonitored,
            ],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));

        Assert.Equal(6, ComposedAdds.All().Count);

        // The filter is on the verb class rather than on the registration, so a grabbing capability a
        // generation holds contributes no case to a list of bodies asserted non-grabbing. Which
        // generations hold each one is written out, because the two differ: the entity search is
        // registered on both and the per-scene search on the newer alone.
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

    /// <summary>
    /// No add carries an acquisition-suppressing spelling its own resource does not declare.
    /// </summary>
    /// <remarks>
    /// The three resources an add can name declare three different spellings, and the older
    /// generation declares two more. A body carrying one its resource does not declare is composed
    /// for a schema other than the one it is being sent to: the instance discards it, so this product
    /// would be reading a suppression it never applied.
    /// </remarks>
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

    /// <summary>
    /// Every enumerated add carries every suppression spelling its resource declares, each PRESENT as
    /// a member and each false.
    /// </summary>
    /// <remarks>
    /// Presence is asserted apart from the value. Omission happened to be safe on the builds this was
    /// measured against, and that is a property of those builds rather than of the contract.
    /// </remarks>
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

    /// <summary>
    /// No enumerated add carries one of its suppression spellings true and another false.
    /// </summary>
    /// <remarks>
    /// Every spelling is read into one comparison rather than checked one at a time, because two
    /// independent assertions are each satisfiable by a body the other one would refuse.
    /// </remarks>
    [Fact]
    public void NoEnumeratedAddCarriesOneSpellingTrueAndAnotherFalse()
        => Assert.All(
            ComposedAdds.All(),
            added => Assert.Equal(
                added.SuppressionPaths.Select(_ => (bool?)false).ToArray(),
                added.SuppressionPaths
                    .Select(path => ComposedAdds.At(added.Body, path)?.GetValue<bool>())
                    .ToArray()));

    /// <summary>
    /// No body composed on a path that must not acquire names a grabbing command.
    /// </summary>
    /// <remarks>
    /// Searched as serialised text rather than at a known member, so a name nested at any depth is
    /// caught. A composed command body is not the only way a name could arrive.
    /// </remarks>
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

    /// <summary>
    /// No body composed on a path that must not acquire carries an acquisition-suppressing spelling
    /// set true, in either generation's spellings.
    /// </summary>
    /// <remarks>
    /// Absence and false are both admissible here, because the flag flips and the catalogue refreshes
    /// carry no such member at all: what is refused is a member present and TRUE. The adds are
    /// asserted present-and-false separately, which is a stronger rule those bodies can keep.
    /// <para>
    /// Both generations' spellings are read over every body. A body composed by cloning what an
    /// instance answered carries whatever that instance holds rather than what this product wrote, so
    /// the flag reaching a request is not decided by which generation's projector composed it.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// A scope change overwrites every suppression spelling the resource it is composed over carried,
    /// each still present as a member and each false.
    /// </summary>
    /// <remarks>
    /// The resource is what the instance answered a read with, so it carries the user's own flags: an
    /// entity added in the instance's interface with search-on-add ticked holds them true, and a clone
    /// that left them alone would re-assert them on a request this product originated.
    /// <para>
    /// What the resource carried is asserted first, so a fixture quietly losing either spelling fails
    /// here rather than making the rest vacuous. Overwritten rather than removed, and presence is
    /// asserted apart from the value: removal would rest on the instance defaulting an absent member
    /// to false, which was never measured.
    /// </para>
    /// </remarks>
    [Fact]
    public void AScopeChangeOverwritesBothSuppressionSpellingsOnWhatTheInstanceHeld()
    {
        // The two the newer generation's resources declare between them, which is what a resource
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

    /// <summary>
    /// The scene monitor body carries the flag and no other member at all.
    /// </summary>
    /// <remarks>
    /// The member set is asserted rather than a spelling's absence, so a field the instance holds for
    /// that scene cannot ride out on this request whatever it is called. The scene itself is named by
    /// the route's own segment.
    /// <para>
    /// Read off the composed body, so no search flag is absent by an omission default nobody
    /// measured.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// A whole monitor gesture on each kind leaves no grabbing-class verb at any position.
    /// </summary>
    /// <remarks>
    /// Every index rather than the last: a grab issued BEFORE the add would be just as acquiring, and
    /// an assertion reading only the final entry would not see it. The class of each recorded verb is
    /// read out of the transcribed table by indexer, so a verb nobody wrote down fails here too.
    /// <para>
    /// Paired with an assertion that the log holds an acting verb, so a gesture that reached the
    /// instance not at all cannot satisfy this.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Every mounted entity verb except the search reaches no grabbing-class verb at any position.
    /// </summary>
    /// <remarks>
    /// Every index of each ordered verb log rather than its last entry: a grab issued BEFORE an act
    /// would be just as acquiring, and an assertion reading only the final entry would not see it.
    /// <para>
    /// The mounted set comes from the emitted document rather than from a list written here, so a
    /// verb mounted later is covered without an edit. The subtraction is asserted to have exactly one
    /// member, so it cannot quietly grow to cover a second grabbing route.
    /// </para>
    /// <para>
    /// Each verb is driven on its own host and its log is asserted non-empty, so a verb that reached
    /// the instance not at all cannot satisfy this. The union is asserted to hold an acting verb for
    /// the same reason at the level of the whole case.
    /// </para>
    /// </remarks>
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

            // Driven to COMPLETION: a verb whose work is enqueued has issued nothing yet, and the
            // requests that would grab are the ones the run makes rather than the ones the route does.
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

    /// <summary>
    /// The grabbing filter runs over a non-empty set, and every capability in it is really held.
    /// </summary>
    /// <remarks>
    /// The enumeration above excludes the grabbing capabilities from the bodies it asserts
    /// non-grabbing. An EMPTY exclusion list would make that filter a no-op and every
    /// <c>Assert.All</c> over it vacuous, which is the shape a list emptied by an edit would take.
    /// Each member is also asserted really held, so a capability nothing registers cannot be sitting
    /// in it standing for a real one.
    /// </remarks>
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

    /// <summary>
    /// The role that can make an instance download is reached only by asking for it BY NAME, and a
    /// caller that asks is forced to say what happens when it is absent.
    /// </summary>
    /// <remarks>
    /// Both generations now hold the capability, so absence is no longer what keeps a monitoring path
    /// from grabbing. What does is that the member lives on that role alone and no monitoring path
    /// obtains it, which the whole-gesture case above asserts behaviourally. A generation this product
    /// does not manage holds nothing, and is what a refusal is asserted over.
    /// </remarks>
    [Fact]
    public void TheGrabbingRoleIsReachedOnlyByAskingForItByName()
    {
        Assert.All(
            ComposedAdds.Generations,
            generation =>
            {
                var capabilities = GenerationCapabilities.For(
                    generation,
                    WhisparrRoleSet.From(
                        new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "{}"))));

                Assert.NotNull(
                    capabilities.Obtain<IWhisparrSearchGrabbing>()
                        .Match<IWhisparrSearchGrabbing?>(held => held, _ => null));
            });

        var unmanaged = GenerationCapabilities.For((WhisparrGeneration)(-1));

        Assert.Empty(unmanaged.Held);
        Assert.Null(
            unmanaged.Obtain<IWhisparrSearchGrabbing>()
                .Match<IWhisparrSearchGrabbing?>(held => held, _ => null));
    }

    /// <summary>
    /// Every mounted per-scene verb except the search reaches no grabbing-class verb at any position.
    /// </summary>
    /// <inheritdoc cref="EveryMountedEntityVerbButTheSearchReachesNoGrabbingVerb" path="/remarks"/>
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
