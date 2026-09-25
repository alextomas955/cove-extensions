using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Scene;
using WhisparrSync.Tests.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Invariants;

// Transcribed from the requirement rather than gathered from the tests. A list gathered from the
// tests would agree with them however many were deleted.
internal static class SafetyInvariant
{
    public const string Trait = "Invariant";

    public const string OneInboundPath = "one inbound path";

    public const string NothingMovedOrDeleted = "nothing moved or deleted in a Whisparr root";

    public const string NoAutoRetriedGrab = "a grab verb is never auto-retried";

    public const string EveryAddIsNonGrabbing = "every add is non-grabbing";

    public const string OnlyAnExplicitSearchGrabs = "only an explicit search grabs";

    public const string EveryMutationIsOriginTagged = "every mutation is origin-tagged and idempotent";

    public const string NothingGrowsWithTheLibrary = "nothing stored or returned grows with the library";

    public static string[] All =>
    [
        OneInboundPath,
        NothingMovedOrDeleted,
        NoAutoRetriedGrab,
        EveryAddIsNonGrabbing,
        OnlyAnExplicitSearchGrabs,
        EveryMutationIsOriginTagged,
        NothingGrowsWithTheLibrary,
    ];
}

// Every member of the outbound seam and the class of work each does, transcribed by hand. A
// member added to the seam is absent here until someone writes it down, and
// TheOutboundSeamDeclaresExactlyTheMembersThisProductCanCall refuses the omission. The lists
// further down are transcribed for the same reason. Only the classifications a name does not give
// away are annotated.
internal static class OutboundSeam
{
    public static IReadOnlyDictionary<string, WhisparrVerbClass> VerbClassByMember { get; } =
        new Dictionary<string, WhisparrVerbClass>(StringComparer.Ordinal)
        {
            [nameof(IWhisparrClient.ReadNotificationSchemaAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrClient.ListNotificationsAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrClient.ReadRootFoldersAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrClient.ReadQualityProfilesAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrClient.ReadHistoryAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrClient.ReadCommandAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrClient.CreateNotificationAsync)] = WhisparrVerbClass.Configure,
            [nameof(IWhisparrClient.UpdateNotificationAsync)] = WhisparrVerbClass.Configure,
            [nameof(IWhisparrStudioActing.ReadStudioAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrStudioActing.AddMonitoredStudioAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrStudioActing.SetStudioMonitoredAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrStudioActing.SetStudioScopeAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrPerformerActing.ReadPerformerAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrPerformerActing.AddMonitoredPerformerAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrPerformerActing.SetPerformerMonitoredAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrMissingSceneActing.AddSceneAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync)] = WhisparrVerbClass.Act,
            // Its body sets both of that generation's search flags false and monitors nothing.
            [nameof(IWhisparrSiteRegistrationActing.RegisterSiteAsync)] = WhisparrVerbClass.Act,
            // The request names no transfer parameter, which leaves the library's own files where
            // they are.
            [nameof(IWhisparrSiteRegistrationActing.MoveSiteRootAsync)] = WhisparrVerbClass.Act,
            // Asks the instance to re-read the path it already holds. No body, no file, no
            // registration.
            [nameof(IWhisparrSiteRegistrationActing.RefreshSiteCatalogueAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync)] = WhisparrVerbClass.Grab,
            [nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync)] = WhisparrVerbClass.Grab,
            [nameof(IWhisparrSceneStatusReading.ReadEntityPresenceAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync)] = WhisparrVerbClass.Read,
            // Answers only entries the instance holds. It composes no command name and starts
            // nothing.
            [nameof(IWhisparrSceneStatusReading.ReduceHeldScenesAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrSceneMonitorActing.SetSceneMonitoredAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrSceneExclusionActing.AddSceneExclusionAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrSceneExclusionActing.RemoveSceneExclusionAsync)] = WhisparrVerbClass.Act,
            // Answers only rows the site already holds, for the reason above.
            [nameof(IWhisparrSiteSceneReading.ReduceSiteSceneRowsAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrInstanceFilesystemReading.ReadInstanceFolderAsync)] = WhisparrVerbClass.Read,
        };

    public static IReadOnlyList<Type> SeamInterfaces { get; } =
    [
        typeof(IWhisparrClient),
        typeof(IWhisparrStudioActing),
        typeof(IWhisparrPerformerActing),
        typeof(IWhisparrMissingSceneActing),
        typeof(IWhisparrSiteRegistrationActing),
        typeof(IWhisparrReflectOwnedActing),
        typeof(IWhisparrSearchGrabbing),
        typeof(IWhisparrSceneSearchGrabbing),
        typeof(IWhisparrSceneStatusReading),
        typeof(IWhisparrSceneMonitorActing),
        typeof(IWhisparrSceneExclusionActing),
        typeof(IWhisparrSiteSceneReading),
        typeof(IWhisparrInstanceFilesystemReading),
    ];

    public static IEnumerable<string> MembersOf(WhisparrVerbClass verbClass)
        => VerbClassByMember
            .Where(member => member.Value == verbClass)
            .Select(member => member.Key)
            .Order();

    public static IEnumerable<Type> SeamsDeclaring(string member)
        => SeamInterfaces.Where(seam => seam
            .GetMethods()
            .Any(method => string.Equals(method.Name, member, StringComparison.Ordinal)));
}

// The safety invariants reachable by driving this product, over doubles that record every
// request's arguments. Those about a capability this product does not hold are in
// AbsentCapabilityTests, asserted as absence.
public sealed class SafetyInvariantTests
{
    // The one route this extension mounts that answers a caller holding no Cove permission. A
    // single value rather than a list, so a second anonymous route fails here.
    private const string InboundRoute = "/api/extensions/com.alextomas955.whisparrsync/callback";

    [Fact]
    public void EverySafetyInvariantHasATestInThisGroup()
    {
        var covered = typeof(SafetyInvariantTests).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(SafetyInvariantTests).Namespace)
            .SelectMany(type => type.GetMethods())
            .SelectMany(method => method.CustomAttributes)
            .Where(attribute => attribute.AttributeType == typeof(TraitAttribute)
                && attribute.ConstructorArguments.Count == 2
                && (string?)attribute.ConstructorArguments[0].Value == SafetyInvariant.Trait)
            .Select(attribute => (string)attribute.ConstructorArguments[1].Value!)
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(SafetyInvariant.All.Order().ToList(), covered);
    }

    // The count itself is asserted beside the route table in EndpointPermissionTests, which boots
    // the same registrations. Named here so the invariant has a case carrying its trait.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OneInboundPath)]
    public void TheInboundPathIsTheCallbackAndNothingElse()
        => Assert.Equal("/api/extensions/com.alextomas955.whisparrsync/callback", InboundRoute);

    // One assertion over a declared union rather than one per interface, so a seam interface left
    // out of OutboundSeam.SeamInterfaces fails the count below instead of slipping past. No name is
    // duplicated across the union, so two interfaces declaring one name fail here too.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingMovedOrDeleted)]
    public void TheOutboundSeamDeclaresExactlyTheMembersThisProductCanCall()
    {
        Assert.Equal(13, OutboundSeam.SeamInterfaces.Count);

        Assert.Equal(
            OutboundSeam.VerbClassByMember.Keys.Order().ToList(),
            OutboundSeam.SeamInterfaces
                .SelectMany(seam => seam.GetMethods())
                .Select(method => method.Name)
                .Order()
                .ToList());
    }

    // Driven over a connection that reaches nothing, which is the one failure a read is re-issued
    // after. Each grabbing member leaves one attempt behind: a second would be a second download of
    // the same entity. v2 searches one entity per command, so one identifier is enough there too.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NoAutoRetriedGrab)]
    public async Task AnEntitySearchThatReachedNothingIsNotIssuedASecondTime(
        WhisparrGeneration generation)
    {
        var handler = BodyRecordingHandler.ReachingNothing();
        var grabbing = (IWhisparrSearchGrabbing)TestWhisparrClient.Over(
            handler, generation: generation);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => grabbing.SearchMonitoredAsync(WhisparrEntityKind.Studio, [4], TestCt));

        Assert.Single(handler.Requests);
    }

    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NoAutoRetriedGrab)]
    public async Task ASceneSearchThatReachedNothingIsNotIssuedASecondTime()
    {
        var handler = BodyRecordingHandler.ReachingNothing();
        var grabbing = (IWhisparrSceneSearchGrabbing)TestWhisparrClient.Over(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => grabbing.SearchSceneAsync(41, TestCt));

        Assert.Single(handler.Requests);
    }

    // The case list is derived from the per-generation capability table, so a combination
    // registered later is covered. Presence is asserted apart from the value: an absent member and
    // a false one read the same, and the instance's default is not this product's to rely on.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryAddIsNonGrabbing)]
    public void EveryAddThisProductCanComposeSuppressesAcquisitionWhereItsResourceDeclaresIt()
    {
        var composed = ComposedAdds.All();

        Assert.NotEmpty(composed);
        Assert.All(
            composed,
            added =>
            {
                Assert.NotEmpty(added.SuppressionPaths);
                Assert.Equal(
                    added.SuppressionPaths.Select(_ => (bool?)false).ToArray(),
                    added.SuppressionPaths
                        .Select(path => ComposedAdds.At(added.Body, path)?.GetValue<bool>())
                        .ToArray());
            });

        // Every registered capability is classified, so a combination registered later reaches the
        // enumeration's own refusal rather than escaping it.
        Assert.All(
            ComposedAdds.Generations,
            generation => Assert.NotNull(ComposedAdds.On(generation)));
    }

    // Every body a monitor, unmonitor, scope change or add composes is searched as serialised text
    // for each transcribed grabbing command name.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OnlyAnExplicitSearchGrabs)]
    public void NoBodyOffAMonitoringPathCanNameAGrabbingCommand()
    {
        var bodies = ComposedAdds.EveryNonGrabbingBody();

        Assert.NotEmpty(bodies);
        Assert.All(
            bodies,
            body => Assert.All(
                ComposedAdds.GrabbingCommandNames,
                name => Assert.DoesNotContain(name, body.ToJsonString(), StringComparison.Ordinal)));

        // The member that can name one is declared on that role alone, so no monitoring call site
        // holds an implementation to reach it through.
        Assert.Equal(
            [typeof(IWhisparrSearchGrabbing)],
            OutboundSeam.SeamsDeclaring(nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync)));
    }

    // A call site that never obtains one of these two roles cannot express a download. The
    // guarantee is not that nothing can grab: two named gestures reach one grabbing member each.
    // Each role declares exactly one member, so neither can grow a second verb unnoticed.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OnlyAnExplicitSearchGrabs)]
    public void TwoSeamMembersGrabAndEachIsDeclaredOnAGrabbingRoleOfItsOwn()
    {
        Assert.Equal(
            [
                nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync),
                nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync),
            ],
            OutboundSeam.MembersOf(WhisparrVerbClass.Grab));

        Assert.Equal(
            [typeof(IWhisparrSearchGrabbing)],
            OutboundSeam.SeamsDeclaring(nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync)));
        Assert.Equal(
            [typeof(IWhisparrSceneSearchGrabbing)],
            OutboundSeam.SeamsDeclaring(nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync)));

        Assert.Single(typeof(IWhisparrSearchGrabbing).GetMethods());
        Assert.Single(typeof(IWhisparrSceneSearchGrabbing).GetMethods());
    }

    // Whatever a caller intended, the filesystem seam declares no member that moves, renames,
    // deletes, opens or writes.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingMovedOrDeleted)]
    public void TheFilesystemSeamDeclaresOnlyTheProbe()
    {
        Assert.Equal(
            [nameof(IImportPathPort.Probe)],
            typeof(IImportPathPort).GetMethods().Select(method => method.Name));
    }

    // The backstop's own reading is asserted first: a pass that walked nowhere would leave an empty
    // log, and an empty log satisfies every emptiness assertion below it.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingMovedOrDeleted)]
    public async Task AWholeIngestAndABackstopPassLeaveOnlyReadClassCallsBehind()
    {
        var ingest = new Ingest();

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync(Ingest.ReportedPath));
        Assert.Equal(
            ImportOutcome.RefusedPathOutsideEveryReportedRoot,
            await ingest.DeliverAsync(Ingest.PathUnderNoReportedRoot));
        Assert.Equal(ImportOutcome.RefusedNotFound, await ingest.DeliverAsync(Ingest.PathOnNoDisk));
        Assert.Equal(
            ImportOutcome.RefusedAmbiguous, await ingest.DeliverAsync(Ingest.PathUnderTwoLibraryRoots));

        var pass = await ingest.BackstopAsync();
        Assert.Equal(BackstopPassOutcome.Walked, pass.Outcome);
        Assert.Equal(1, pass.Imported);

        Assert.NotEmpty(ingest.Client.Verbs);
        Assert.All(
            ingest.Client.Verbs,
            verb => Assert.Equal(WhisparrVerbClass.Read, OutboundSeam.VerbClassByMember[verb]));

        // The arguments rather than the count. A request that acts on the instance carries a body and
        // names what it acts on, and neither is present on anything this path sent.
        Assert.All(
            ingest.Client.Notifications,
            call =>
            {
                Assert.Equal(Ingest.BaseAddress, call.BaseAddress);
                Assert.Null(call.Id);
                Assert.Null(call.Body);
            });
        Assert.All(
            ingest.Client.Histories,
            call =>
            {
                Assert.Equal(Ingest.BaseAddress, call.BaseAddress);
                Assert.Equal(Ingest.ApiKey, call.ApiKey);
                Assert.Equal(BackstopPass.PageSize, call.PageSize);
            });

        Assert.Equal(
            [nameof(IImportPathPort.Probe)],
            ingest.Paths.Operations.Select(operation => operation.Operation).Distinct());
    }

    // The refusals are asserted by cause rather than counted, so a run in which the core answered
    // the same refusal to everything is not read as three branches.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingMovedOrDeleted)]
    public async Task AnIngestThatRegisteredNothingStillSendsOnlyReadClassCalls()
    {
        var ingest = new Ingest();

        Assert.Equal(ImportOutcome.RefusedNotFound, await ingest.DeliverAsync(Ingest.PathOnNoDisk));
        Assert.Equal(
            ImportOutcome.RefusedPathOutsideEveryReportedRoot,
            await ingest.DeliverAsync(Ingest.PathUnderNoReportedRoot));
        Assert.Equal(
            ImportOutcome.RefusedAmbiguous, await ingest.DeliverAsync(Ingest.PathUnderTwoLibraryRoots));

        Assert.Empty(ingest.Library.Imported);
        Assert.NotEmpty(ingest.Client.Verbs);
        Assert.All(
            ingest.Client.Verbs,
            verb => Assert.Equal(WhisparrVerbClass.Read, OutboundSeam.VerbClassByMember[verb]));
        Assert.Equal(
            [nameof(IImportPathPort.Probe)],
            ingest.Paths.Operations.Select(operation => operation.Operation).Distinct());
    }

    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NoAutoRetriedGrab)]
    public void OnlyTheReadClassIsRetriedAndAnUnlistedClassGetsOneAttempt()
    {
        Assert.Equal(
            [WhisparrVerbClass.Read],
            Enum.GetValues<WhisparrVerbClass>()
                .Where(verbClass => WhisparrRetryPolicy.AttemptsFor(verbClass) > WhisparrRetryPolicy.NoRetry));

        Assert.Equal(
            WhisparrRetryPolicy.NoRetry, WhisparrRetryPolicy.AttemptsFor(UnlistedVerbClass));
    }

    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingMovedOrDeleted)]
    public async Task ReplayingOneDeliveryMutatesNoPathAndCreatesNothingTheSecondTime()
    {
        var ingest = new Ingest();

        Assert.Equal(
            ImportOutcome.Imported, await ingest.DeliverAsync(Ingest.ReportedPath, Ingest.RemoteId));
        var probesAfterTheFirst = ingest.Paths.Operations.Count;

        // The state the second delivery meets: the host registered the file, which is what the live
        // dedupe reads back.
        ingest.Library.Held[Ingest.VerifiedPath] = new HeldFile(Ingest.HeldVideoId);

        Assert.Equal(
            ImportOutcome.AlreadyHeld, await ingest.DeliverAsync(Ingest.ReportedPath, Ingest.RemoteId));

        Assert.Equal((Ingest.VerifiedPath, (int?)null), Assert.Single(ingest.Library.Imported));
        Assert.Single(ingest.Library.Stamped);
        Assert.Empty(ingest.Library.Detached);
        Assert.Equal(
            [nameof(IImportPathPort.Probe)],
            ingest.Paths.Operations.Select(operation => operation.Operation).Distinct());

        // The second delivery derived its answer rather than remembering the first's.
        Assert.True(ingest.Paths.Operations.Count > probesAfterTheFirst);
    }

    // The pass set is asserted exactly, so a third pass fails here rather than travelling under an
    // enumeration written for two. A run reaching a whole library is the one gesture whose
    // acquisition cost would be the size of the library.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryAddIsNonGrabbing)]
    public void NeitherPassALibraryRunMakesRegistersThroughAnythingButANonGrabbingAdd()
    {
        Assert.Equal([SyncRegisters.Scenes, SyncRegisters.Sites], Enum.GetValues<SyncRegisters>());

        Assert.Equal(
            [WhisparrVerbClass.Act, WhisparrVerbClass.Act],
            new[]
            {
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                nameof(IWhisparrSiteRegistrationActing.RegisterSiteAsync),
            }.Select(member => OutboundSeam.VerbClassByMember[member]));

        Assert.All(
            RegisteringBodies(),
            registering =>
            {
                Assert.Equal(
                    registering.Suppression.Select(_ => (bool?)false).ToArray(),
                    registering.Suppression
                        .Select(path => ComposedAdds.At(registering.Body, path)?.GetValue<bool>())
                        .ToArray());

                Assert.All(
                    ComposedAdds.GrabbingCommandNames,
                    name => Assert.DoesNotContain(
                        name, registering.Body.ToJsonString(), StringComparison.Ordinal));
            });
    }

    // The composition is compared as text, so a re-run is proved to send what the first run sent.
    // The already-held count is the load-bearing one: a second offer counted as registered would be
    // a duplicate this product created and then reported as work.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryMutationIsOriginTagged)]
    public async Task OfferingTheSameLibraryTwiceComposesTheSameRequestAndRegistersNothingAgain()
    {
        Assert.Equal(
            ComposedBody.Of(V3BodyProjector.AddScene(SyncScene, SyncDefaults)).ToJsonString(),
            ComposedBody.Of(V3BodyProjector.AddScene(SyncScene, SyncDefaults)).ToJsonString());

        var held = new HashSet<string>(StringComparer.Ordinal);
        var offered = new List<string>();

        async Task<SyncLibraryRun> RunOnceAsync()
            => await SyncLibraryPlanner.RunAsync(
                SyncRegisters.Scenes,
                new SyncLibrarySource<string>(
                    Streamed,
                    identity => identity,
                    (identity, _) => { offered.Add(identity); return Task.FromResult(SyncRegistration.Offered(held.Add(identity) ? SceneAccepted : SceneAlreadyHeld)); },
                    null),
                new RecordingJobProgress(),
                TestCt);

        var first = await RunOnceAsync();
        var second = await RunOnceAsync();

        Assert.Equal(OfferedLibrary.Length, first.Registered);
        Assert.Equal(0, first.AlreadyHeld);

        Assert.Equal(0, second.Registered);
        Assert.Equal(OfferedLibrary.Length, second.AlreadyHeld);
        Assert.Equal(0, second.Refused);
        Assert.Equal(0, second.Monitored);

        Assert.Equal([.. OfferedLibrary, .. OfferedLibrary], offered);
    }

    // Asserted on the declared shapes rather than on what one run put in them: a run observed at
    // one library size says nothing about the next. A member bounded by something other than the
    // library is allowed only by naming it below with its bound.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingGrowsWithTheLibrary)]
    public void NeitherTheCountAnswerNorEitherRunsResultCarriesAMemberThatGrowsWithTheLibrary()
        => Assert.All(
            new[]
            {
                typeof(SyncPreviewView),
                typeof(SyncPreviewRead),
                typeof(SyncEnqueued),
                typeof(SyncLibraryRun),
                typeof(SceneMonitorTally),
            },
            shape =>
            {
                var unbounded = shape.GetProperties()
                    .Where(property => property.PropertyType != typeof(string)
                        && typeof(System.Collections.IEnumerable)
                            .IsAssignableFrom(property.PropertyType))
                    .Select(property => $"{shape.Name}.{property.Name}")
                    .Where(member => !BoundedBySomethingOtherThanTheLibrary.Contains(member))
                    .ToList();

                Assert.Empty(unbounded);
            });

    // The library roots hold one entry per root an operator configured by hand, each named once,
    // and carry root names only: never an entry, a folder or a file. A member added here has to
    // state its own bound the same way.
    private static readonly HashSet<string> BoundedBySomethingOtherThanTheLibrary =
        new(StringComparer.Ordinal)
        {
            $"{nameof(SyncLibraryRun)}.{nameof(SyncLibraryRun.RootsLeftBehind)}",
        };

    private const string SyncScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";

    private const string SecondSyncScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private const string ThirdSyncScene = "7b1e4d90-2c3a-4f81-95d6-0a8b7c6e5f43";

    // The library both runs walk, in the order the stream yields it.
    private static readonly string[] OfferedLibrary = [SyncScene, SecondSyncScene, ThirdSyncScene];

    // Streamed the way the run's own identifier port hands it over.
    private static async IAsyncEnumerable<string> Streamed(
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var identity in OfferedLibrary)
        {
            ct.ThrowIfCancellationRequested();
            yield return identity;
            await Task.Yield();
        }
    }

    private static AddDefaults SyncDefaults => new(4, "/config/library");

    // v2 refuses the quality profile v3 accepts, so its defaults name a different one.
    private static AddDefaults SyncV2Defaults => new(1, "/config/library");

    // What one instance answered a scene it took, and one it already held.
    private static WhisparrResponse SceneAccepted
        => RecordingWhisparrCore.Json(
            201, ProbeFixtures.Read("whisparr-v3-3.3.8.1097-scene-add-accepted.json"));

    private static WhisparrResponse SceneAlreadyHeld
        => RecordingWhisparrCore.Json(
            400, ProbeFixtures.Read("whisparr-v3-3.3.8.1097-scene-add-already-held.json"));

    // The v2 identifiers are the ones that generation's own lookup was measured answering, because
    // its add is composed from the number the lookup returned rather than from the one the library
    // holds.
    private static IReadOnlyList<(JsonObject Body, IReadOnlyList<string> Suppression)>
        RegisteringBodies() =>
        [
            (ComposedBody.Of(V3BodyProjector.AddScene(SyncScene, SyncDefaults)),
                [ComposedAdds.SceneSuppression]),
            (ComposedV2Body.Of(V2BodyProjector.RegisterSite(3372, SyncV2Defaults)),
                ComposedAdds.V2Suppression),
        ];

    // A class the retry table does not list, cast from a value the enum does not declare. That is
    // how a class added without a table entry behaves.
    private const WhisparrVerbClass UnlistedVerbClass = (WhisparrVerbClass)(-1);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The whole ingest path over doubles, with one recorder at the outbound seam. The reported-root
    // read, the ingest core and the backstop pass are the shipped types, so every request they make
    // leaves through the one recorded client.
    private sealed class Ingest
    {
        public const string ApiKey = "5f5f5f5f5f5f5f5f5f5f5f5f5f5f5f5f";
        public const string RemoteId = "e1a5c0d2-0000-4000-8000-000000000008";
        public const int HeldVideoId = 1;

        // A reported file present under exactly one host library root.
        public const string ReportedPath = WhisparrRoot + "/scene.mp4";

        // Where the guard resolves ReportedPath to.
        public const string VerifiedPath = FirstLibraryRoot + "/scene.mp4";

        // A reported file the instance declares no root for.
        public const string PathUnderNoReportedRoot = "/elsewhere/scene.mp4";

        // A reported file no candidate for which is on disk.
        public const string PathOnNoDisk = WhisparrRoot + "/absent.mp4";

        // A reported file present under both host library roots.
        public const string PathUnderTwoLibraryRoots = WhisparrRoot + "/twice.mp4";

        public static readonly Uri BaseAddress = new(Address + "/");

        private const string Address = "http://whisparr:6969";
        private const string WhisparrRoot = "/whisparr-media";
        private const string FirstLibraryRoot = "/data";
        private const string SecondLibraryRoot = "/data2";
        private const string BackstopTail = "/backstop.mp4";
        private const long FileSize = 10;

        private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

        private readonly OptionsStore _options;
        private readonly FollowUpScanCoalescer _followUp;
        private readonly IReportedRootPort _reportedRoots;
        private readonly ICredentialPort _credentials =
            new RecordingCredentialPort().Holding(WhisparrGeneration.V3, Address, ApiKey);

        public Ingest()
        {
            Client = new RecordingWhisparrV3Client(
                RecordingWhisparrCore.Json(200, "[]"),
                new WhisparrBinding(WhisparrGeneration.V3, Ingest.BaseAddress, ApiKey));
            Client.Answering(
                nameof(IWhisparrClient.ReadRootFoldersAsync),
                RecordingWhisparrCore.Json(
                    200, new JsonArray(new JsonObject { ["path"] = WhisparrRoot }).ToJsonString()));
            Client.Answering(nameof(IWhisparrClient.ReadHistoryAsync), HistoryNaming(BackstopTail));

            _options = new OptionsStore(Store);
            _options
                .SaveAsync(
                    new WhisparrSyncOptions
                    {
                        SelectedGeneration = WhisparrGeneration.V3,
                        V3 = new WhisparrSyncGenerationConnection
                        {
                            Address = Address,
                            BackstopWatermarkUtc = Now.AddHours(-1),
                        },
                    },
                    TestCt)
                .GetAwaiter()
                .GetResult();

            var clock = new FixedClock(Now);
            _followUp = new FollowUpScanCoalescer(clock, NullLogger.Instance);
            _reportedRoots = new ReportedRootPort(
                new FixedInstanceFactory(Client),
                _credentials,
                new ReportedRootCache(clock),
                NullLogger.Instance);
        }

        public FakeStore Store { get; } = new();

        public RecordingWhisparrCore Client { get; }

        public RecordingLibrary Library { get; } =
            new(reached: true, [FirstLibraryRoot, SecondLibraryRoot]);

        public RecordingPathPort Paths { get; } =
            new()
            {
                Present =
                {
                    [VerifiedPath] = FileSize,
                    [FirstLibraryRoot + BackstopTail] = FileSize,
                    [FirstLibraryRoot + "/twice.mp4"] = FileSize,
                    [SecondLibraryRoot + "/twice.mp4"] = FileSize,
                },
            };

        public Task<ImportOutcome> DeliverAsync(string reportedPath, string? remoteId = null)
            => Core().IngestAsync(
                new ImportCandidate(
                    WhisparrGeneration.V3, "Download", reportedPath, FileSize, remoteId),
                TestCt);

        // The one gate every write in a case goes through, as the container has it.
        public OptionsWriteGate Gate { get; } = new();

        public Task<BackstopPassResult> BackstopAsync()
            => new BackstopPass(
                    new WhisparrAccess(
                        _options,
                        _credentials,
                        new FixedInstanceFactory(Client),
                        NullLogger.Instance),
                    Gate,
                    Core(),
                    new FixedClock(Now),
                    _followUp,
                    Library)
                .RunAsync(TestCt);

        // One page holding one import record, naming a file below the reporting root.
        private static WhisparrResponse HistoryNaming(string tail)
            => RecordingWhisparrCore.Json(
                200,
                new JsonObject
                {
                    ["records"] = new JsonArray(
                        new JsonObject
                        {
                            ["eventType"] = HistoryProjector.ImportedEventType,
                            ["date"] = Now.ToString("O"),
                            ["data"] = new JsonObject { ["importedPath"] = WhisparrRoot + tail },
                        }),
                }.ToJsonString());

        private ImportCore Core()
            => new ImportCore(
                _reportedRoots,
                Library,
                Paths,
                new OptionsWriting(_options, Gate),
                _followUp,
                new FixedClock(Now),
                NullLogger.Instance);
    }

    // The host serialises every stored value on one bulk route, so one oversized value breaks the
    // whole settings page and survives a reinstall. The settings blob is written by the options
    // slice, which is not in this set and is bounded by its own record.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingGrowsWithTheLibrary)]
    public void NoTypeUnderTheDerivationCanReachTheStoredValueSurface()
    {
        string[] slices =
        [
            "WhisparrSync.Missing",
            "WhisparrSync.Providers",
            "WhisparrSync.Jobs",
        ];

        var holders = typeof(MissingPagePlanner).Assembly
            .GetTypes()
            .Where(type => slices.Contains(type.Namespace, StringComparer.Ordinal))
            .Where(type => CanReach(type, typeof(IExtensionStore)))
            .Select(type => type.FullName!)
            .Order()
            .ToList();

        Assert.Empty(holders);
    }

    // Transcribed, so a collection member added without a decision about what bounds it fails here.
    // What each is bounded by is asserted by driving the derivation below: a declared type says
    // nothing about the count an implementation puts in it.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingGrowsWithTheLibrary)]
    public void EveryCollectionACatalogueRouteAnswersWithWasWrittenDown()
        => Assert.Equal(
            ["Cards", "Facets", "Sorts"],
            typeof(MissingPageView)
                .GetProperties()
                .Where(property => property.PropertyType != typeof(string)
                    && typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType))
                .Select(property => property.Name)
                .Order()
                .ToList());

    // The page is never topped back up. Fetching more to fill a gap is unbounded where a reader
    // owns most of an entity, which is the shape this asserts the derivation does not have.
    [Theory]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingGrowsWithTheLibrary)]
    [InlineData(0, 0, 40)]
    [InlineData(10, 0, 30)]
    [InlineData(0, 7, 33)]
    [InlineData(10, 7, 23)]
    public async Task APageCarriesAtMostThePerPageItAskedForAndFewerOnceRowsWereRemoved(
        int owned, int excluded, int expected)
    {
        var scenes = PageOfScenes(40);
        var exclusionReading = new RecordingExclusionReading(
            [.. scenes.Skip(owned).Take(excluded).Select(scene => scene.ProviderSceneId)]);

        var view = await DeriveAsync(
            scenes,
            [.. scenes.Take(owned).Select(scene => scene.ProviderSceneId)],
            exclusionReading);

        Assert.Equal(expected, view.Cards.Count);
        Assert.True(view.Cards.Count <= view.PerPage);
    }

    // The instance narrows that route by no parameter, so a read per card would transfer the whole
    // list once per card to answer questions one read already answered.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingGrowsWithTheLibrary)]
    public async Task TheExclusionListIsReadOncePerDerivationAndNeverOncePerCard()
    {
        var scenes = PageOfScenes(40);
        var exclusionReading = new RecordingExclusionReading([]);

        await DeriveAsync(scenes, [], exclusionReading);

        Assert.Equal(1, exclusionReading.Calls);
        var asked = Assert.Single(exclusionReading.AskedAbout);
        Assert.Equal(40, asked.Count);
    }

    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingGrowsWithTheLibrary)]
    // A page of cards costs one read of the entity's own list and no per-scene read: each card's
    // state is the flag on the row it came from. Neither the planner nor the context it plans
    // against can reach the per-scene status surface.
    public async Task APageCostsOneReadAndNeverOnePerCard()
    {
        var scenes = PageOfScenes(40);

        var view = await DeriveAsync(scenes, [], new RecordingExclusionReading([]));

        Assert.Equal(40, view.Cards.Count);
        Assert.False(CanReach(typeof(MissingPagePlanner), typeof(IWhisparrSceneStatusReading)));
        Assert.False(CanReach(typeof(MissingPageContext), typeof(IWhisparrSceneStatusReading)));
    }

    // Fields and constructor parameters alike, so a type taking one and not storing it still
    // counts: it can still hand it on.
    private static bool CanReach(Type type, Type reached)
        => type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic)
            .Any(field => reached.IsAssignableFrom(field.FieldType))
        || type.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => reached.IsAssignableFrom(parameter.ParameterType));

    private static List<ProviderScene> PageOfScenes(int count)
        => [.. Enumerable.Range(0, count)
            .Select(index => new ProviderScene(
                $"scene-{index}", $"Scene {index}", null, null, null, null, [], []))];

    // Driven through its real ports, so the counts a case asserts are the ones actually spent.
    private static Task<MissingPageView> DeriveAsync(
        List<ProviderScene> scenes,
        string[] owned,
        RecordingExclusionReading exclusionReading)
    {
        var catalogue = new StubProviderCatalogue(scenes);
        var planner = new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubEntityIdentities("an-entity"),
                TestProviderCatalogues.Naming(catalogue),
                new StubEntityNames()),
            TestProviderCatalogues.Naming(catalogue),
            new StubOwnedScenes(owned),
            new InstanceCatalogueCache(TimeProvider.System));

        return planner.PlanAsync(
            new MissingPageRequest(
                WhisparrEntityKind.Studio,
                7,
                Page: 1,
                PerPage: 40,
                Sort: null,
                TitleSearch: null,
                Filters: new Dictionary<string, string>(),
                MenusAlreadyHeld: true),
            new MissingPageContext(
                new WhisparrBinding(
                    WhisparrGeneration.V3,
                    new Uri("http://whisparr.invalid:6969"),
                    "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e"),
                new ResolvedProvider("https://stashdb.org/graphql", "a-key", 240),
                exclusionReading,
                new StubInstanceCatalogue(
                    [.. scenes.Select(scene => StubInstanceCatalogue.Scene(scene.ProviderSceneId))])),
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
    }

    private sealed class RecordingExclusionReading(string[] excluded) : IWhisparrSceneExclusionReading
    {
        public int Calls { get; private set; }

        public List<IReadOnlyList<string>> AskedAbout { get; } = [];

        public Task<IReadOnlySet<string>> ReduceExclusionsAsync(
            IReadOnlyCollection<string> providerSceneIds,
            CancellationToken ct)
        {
            Calls++;
            AskedAbout.Add([.. providerSceneIds]);
            return Task.FromResult<IReadOnlySet<string>>(
                providerSceneIds.Where(excluded.Contains).ToHashSet(StringComparer.Ordinal));
        }

        public Task<SceneExclusionLookup> FindSceneExclusionAsync(
            string foreignId, CancellationToken ct)
            => throw new NotSupportedException(
                "A page derivation asks about a whole page at once and never for one row's own "
                    + "identifier.");
    }

    // The operation is recorded by its member name beside the path, so the log answers which
    // operations a path was subjected to rather than how many times it was touched.
    private sealed class RecordingPathPort : IImportPathPort
    {
        public Dictionary<string, long> Present { get; } = [];

        public List<(string Operation, string Path)> Operations { get; } = [];

        public ProbedPath Probe(string path)
        {
            Operations.Add((nameof(Probe), path));
            return Present.TryGetValue(path, out var size)
                ? new ProbedPath(true, size)
                : new ProbedPath(false, null);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
