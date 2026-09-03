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
internal sealed record ComposedAdd(
    WhisparrGeneration Generation,
    WhisparrEntityKind Kind,
    MonitorScope? Scope,
    JsonObject Body);

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
        "SeriesSearch",
        "MissingMoviesSearch",
        "CutoffUnmetMoviesSearch",
        "MissingEpisodeSearch",
        "CutoffUnmetEpisodeSearch",
        "EpisodeSearch",
    ];

    /// <summary>A scene the library holds that an instance's catalogue does not.</summary>
    private const string SceneForeignId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

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
        [WhisparrCapability.SearchMonitored];

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
        V3BodyProjector.SetStudioMonitored(4, monitored: true),
        V3BodyProjector.SetStudioMonitored(4, monitored: false),
        V3BodyProjector.SetPerformerMonitored(11, monitored: true),
        V3BodyProjector.SetPerformerMonitored(11, monitored: false),
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

    /// <summary>
    /// Both spellings <paramref name="generation"/> reads an acquisition-suppressing flag in.
    /// </summary>
    /// <remarks>
    /// Transcribed per generation, and both are asserted every time: one resource family reads a
    /// top-level flag while another reads an add-options member, so a rule stated for one leaves the
    /// other unguarded.
    /// </remarks>
    public static IReadOnlyList<string> SuppressionPathsOn(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => ["searchOnAdd", "addOptions.searchForMovie"],
            WhisparrGeneration.V2 =>
                ["addOptions.searchForMissingEpisodes", "addOptions.searchForCutoffUnmetEpisodes"],
            _ => throw new ArgumentOutOfRangeException(
                nameof(generation),
                generation,
                "No suppression spellings are written down for this generation, so an add composed "
                    + "for it cannot be asserted non-grabbing."),
        };

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

            (WhisparrGeneration.V3, WhisparrCapability.MonitorStudio) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    MonitorScope.FutureScenes,
                    V3BodyProjector.AddStudio(
                        MonitorHost.StudioRemoteIdValue, MonitorScope.FutureScenes, Defaults, Now)),
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    MonitorScope.AllScenes,
                    V3BodyProjector.AddStudio(
                        MonitorHost.StudioRemoteIdValue, MonitorScope.AllScenes, Defaults, Now)),
            ],

            // One case and no scope: the field a future-only scope is expressed through is on the
            // studio resource and on no other, so this kind has no second combination to cover.
            (WhisparrGeneration.V3, WhisparrCapability.MonitorPerformer) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Performer,
                    null,
                    V3BodyProjector.AddPerformer(MonitorHost.PerformerRemoteIdValue, Defaults)),
            ],

            // One catalogue item registered per scene, with the monitor type covering that scene
            // alone and the add method recording that a person asked for it.
            (WhisparrGeneration.V3, WhisparrCapability.RegisterMissingScenes) =>
            [
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    null,
                    V3BodyProjector.AddScene(SceneForeignId, Defaults)),
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
                    V2BodyProjector.AddStudio(
                        3372, "Vixen", "vixen", MonitorScope.FutureScenes, V2Defaults)),
                new ComposedAdd(
                    generation,
                    WhisparrEntityKind.Studio,
                    MonitorScope.AllScenes,
                    V2BodyProjector.AddStudio(
                        3372, "Vixen", "vixen", MonitorScope.AllScenes, V2Defaults)),
            ],

            _ => throw new NotSupportedException(
                $"{generation} holds {capability} and no composed add body for it is enumerated in "
                    + $"{nameof(ComposedAdds)}. Add the combination's body beside the others so the "
                    + "never-search assertions cover it."),
        };

    /// <summary>The resource every scope change is composed over.</summary>
    public static JsonObject Held()
        => (JsonObject)JsonNode.Parse(HeldStudio)!;
}

/// <summary>
/// The behavioural half of the never-search guarantee: what every composed body actually says, over
/// every combination the registered capabilities allow.
/// </summary>
/// <remarks>
/// The type-level half — which members can add and which single member can grab — is asserted in the
/// invariant group. Nothing here reads a status or a count: the subject is the body that would reach
/// an instance and the order in which the requests would leave.
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

        // The filter is on the verb class rather than on the registration, so a grabbing capability
        // both generations now hold contributes no case to a list of bodies asserted non-grabbing.
        Assert.All(
            ComposedAdds.GrabbingCapabilities,
            grabbing => Assert.All(
                ComposedAdds.Generations,
                generation => Assert.Contains(
                    grabbing, GenerationCapabilities.CapabilitiesOf(generation))));
    }

    /// <summary>
    /// The older generation's suppression spellings are not the newer one's, and no body composed for
    /// one generation carries the other's.
    /// </summary>
    /// <remarks>
    /// Both pairs live inside an add-options member on one generation while the other reads one of its
    /// two at the top level, so a rule written in either generation's spellings leaves every body of
    /// the other unguarded. This asserts each generation's enumerated bodies against its own pair AND
    /// against the absence of the other's.
    /// </remarks>
    [Fact]
    public void NoAddCarriesTheOtherGenerationsSuppressionSpellings()
    {
        Assert.All(
            ComposedAdds.Generations,
            generation =>
            {
                var mine = ComposedAdds.SuppressionPathsOn(generation);
                var theirs = ComposedAdds.Generations
                    .Where(other => other != generation)
                    .SelectMany(ComposedAdds.SuppressionPathsOn)
                    .Select(path => path.Split('.')[^1])
                    .ToArray();

                Assert.NotEmpty(ComposedAdds.On(generation));
                Assert.All(
                    ComposedAdds.On(generation),
                    added =>
                    {
                        Assert.All(mine, path => Assert.NotNull(ComposedAdds.At(added.Body, path)));
                        Assert.All(
                            theirs,
                            spelling => Assert.DoesNotContain(
                                spelling, added.Body.ToJsonString(), StringComparison.Ordinal));
                    });
            });
    }

    /// <summary>
    /// Every enumerated add carries both of its generation's suppression spellings, each PRESENT as
    /// a member and each false.
    /// </summary>
    /// <remarks>
    /// Presence is asserted apart from the value. Omission happened to be safe on the builds this was
    /// measured against, and that is a property of those builds rather than of the contract.
    /// </remarks>
    [Fact]
    public void EveryEnumeratedAddCarriesBothSuppressionSpellingsPresentAndFalse()
        => Assert.All(
            ComposedAdds.All(),
            added =>
            {
                var paths = ComposedAdds.SuppressionPathsOn(added.Generation);
                Assert.Equal(2, paths.Count);
                Assert.All(paths, path => Assert.NotNull(ComposedAdds.At(added.Body, path)));
                Assert.All(
                    paths,
                    path => Assert.False(ComposedAdds.At(added.Body, path)!.GetValue<bool>()));
            });

    /// <summary>
    /// No enumerated add carries one suppression spelling true and the other false.
    /// </summary>
    /// <remarks>
    /// Both spellings are read into one comparison rather than checked one at a time, because two
    /// independent assertions are each satisfiable by a body the other one would refuse.
    /// </remarks>
    [Fact]
    public void NoEnumeratedAddCarriesOneSpellingTrueAndTheOtherFalse()
        => Assert.All(
            ComposedAdds.All(),
            added => Assert.Equal(
                new bool?[] { false, false },
                ComposedAdds.SuppressionPathsOn(added.Generation)
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
        var paths = ComposedAdds.Generations
            .SelectMany(ComposedAdds.SuppressionPathsOn)
            .ToArray();

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
    /// A scope change overwrites both suppression spellings on the resource it is composed over, each
    /// present as a member and each false.
    /// </summary>
    /// <remarks>
    /// The resource is what the instance answered a read with, so it carries the user's own flags: a
    /// studio added in the instance's interface with search-on-add ticked holds both spellings true,
    /// and a clone that left them alone would re-assert them on a request this product originated.
    /// <para>
    /// Overwritten rather than removed, and presence is asserted apart from the value. Removal would
    /// rest on the instance defaulting an absent member to false, which was never measured.
    /// </para>
    /// </remarks>
    [Fact]
    public void AScopeChangeOverwritesBothSuppressionSpellingsOnWhatTheInstanceHeld()
    {
        var paths = ComposedAdds.SuppressionPathsOn(WhisparrGeneration.V3);
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
}
