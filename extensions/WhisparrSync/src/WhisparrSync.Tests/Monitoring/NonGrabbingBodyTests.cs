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

    /// <summary>The instance-side values every enumerated add is composed with.</summary>
    private static readonly AddDefaults Defaults = new(4, "/config/library");

    /// <summary>The same, for a generation that refuses a profile the other one accepts.</summary>
    private static readonly AddDefaults V2Defaults = new(1, "/config/library");

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A studio as an instance holds one, for the verbs that read before they write.</summary>
    private const string HeldStudio = """
        {"id":4,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true,
         "afterDate":"2026-09-02","qualityProfileId":4,"rootFolderPath":"/config/library","tags":[7]}
        """;

    /// <summary>Every generation this product can be connected to.</summary>
    public static IReadOnlyList<WhisparrGeneration> Generations { get; } =
        [WhisparrGeneration.V3, WhisparrGeneration.V2];

    /// <summary>Every add body <paramref name="generation"/>'s registered capabilities compose.</summary>
    public static IReadOnlyList<ComposedAdd> On(WhisparrGeneration generation) =>
    [
        .. GenerationCapabilities.CapabilitiesOf(generation)
            .SelectMany(capability => CasesFor(generation, capability))
    ];

    /// <summary>Every add body this product can compose, over every generation.</summary>
    public static IReadOnlyList<ComposedAdd> All() => [.. Generations.SelectMany(On)];

    /// <summary>
    /// Every body composed on a path that must not acquire: the adds, both flag flips and both scope
    /// changes.
    /// </summary>
    public static IReadOnlyList<JsonObject> EveryNonGrabbingBody() =>
    [
        .. All().Select(added => added.Body),
        V3BodyProjector.SetStudioMonitored(4, monitored: true),
        V3BodyProjector.SetStudioMonitored(4, monitored: false),
        V3BodyProjector.SetPerformerMonitored(11, monitored: true),
        V3BodyProjector.SetPerformerMonitored(11, monitored: false),
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

    private static JsonObject Held()
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
        Assert.Equal(3, ComposedAdds.On(WhisparrGeneration.V3).Count);
        Assert.Equal(
            [
                WhisparrCapability.OutOfBandCallbackSecret,
                WhisparrCapability.MonitorStudio,
                WhisparrCapability.MonitorPerformer,
            ],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V3));

        Assert.Equal(2, ComposedAdds.On(WhisparrGeneration.V2).Count);
        Assert.Equal(
            [WhisparrCapability.OutOfBandCallbackSecret, WhisparrCapability.MonitorStudio],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));

        Assert.Equal(5, ComposedAdds.All().Count);
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
    /// The one role that can make an instance download is absent from the set every monitoring call
    /// site obtains its own role from.
    /// </summary>
    /// <remarks>
    /// The set a monitor is acted through is the same one this asks, so this is a fact about the path
    /// under test rather than about a set assembled for the question.
    /// </remarks>
    [Fact]
    public void TheGrabbingRoleIsAbsentFromTheSetEveryMonitorPathObtainsFrom()
        => Assert.All(
            ComposedAdds.Generations,
            generation =>
            {
                var capabilities = GenerationCapabilities.For(
                    generation,
                    WhisparrRoleSet.From(
                        new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "{}"))));

                Assert.DoesNotContain(WhisparrCapability.SearchMonitored, capabilities.Held);
                Assert.Null(
                    capabilities.Obtain<IWhisparrSearchGrabbing>()
                        .Match<IWhisparrSearchGrabbing?>(held => held, _ => null));
            });
}
