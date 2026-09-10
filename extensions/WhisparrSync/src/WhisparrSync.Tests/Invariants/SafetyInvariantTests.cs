using System.Reflection;
using System.Text.Json.Nodes;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
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

/// <summary>The safety invariants this product declares, and the trait that names one.</summary>
/// <remarks>
/// Transcribed from the requirement rather than gathered from the tests. A list gathered from the
/// tests would agree with them however many were deleted.
/// </remarks>
internal static class SafetyInvariant
{
    /// <summary>The trait naming which invariant a test is about.</summary>
    public const string Trait = "Invariant";

    public const string OneInboundPath = "one inbound path";

    public const string NothingMovedOrDeleted = "nothing moved or deleted in a Whisparr root";

    public const string NoAutoRetriedGrab = "a grab verb is never auto-retried";

    public const string EveryAddIsNonGrabbing = "every add is non-grabbing";

    public const string OnlyAnExplicitSearchGrabs = "only an explicit search grabs";

    public const string EveryMutationIsOriginTagged = "every mutation is origin-tagged and idempotent";

    public const string NothingGrowsWithTheLibrary = "nothing stored or returned grows with the library";

    /// <summary>Every invariant this product declares.</summary>
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

/// <summary>Every member of the outbound seam, and the class of work each one does.</summary>
/// <remarks>
/// Transcribed by hand. A member added to the seam is absent here until someone writes it down, and
/// <see cref="SafetyInvariantTests.TheOutboundSeamDeclaresExactlyTheMembersThisProductCanCall"/> is
/// what refuses the omission.
/// </remarks>
internal static class OutboundSeam
{
    public static IReadOnlyDictionary<string, WhisparrVerbClass> VerbClassByMember { get; } =
        new Dictionary<string, WhisparrVerbClass>(StringComparer.Ordinal)
        {
            [nameof(IWhisparrClient.ReadStatusAsync)] = WhisparrVerbClass.Read,
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
            // An add, so it is never retried; non-grabbing, because the body it composes sets both
            // of that generation's search flags false and monitors nothing.
            [nameof(IWhisparrSiteRegistrationActing.RegisterSiteAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync)] = WhisparrVerbClass.Grab,
            [nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync)] = WhisparrVerbClass.Grab,
            [nameof(IWhisparrSceneStatusReading.ReadEntityPresenceAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync)] = WhisparrVerbClass.Read,
            // A read, not a grab: it answers only entries the instance already holds, it composes no
            // command name, and it starts nothing on the instance's side.
            [nameof(IWhisparrSceneStatusReading.ReduceHeldScenesAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrSceneMonitorActing.SetSceneMonitoredAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrSceneExclusionActing.AddSceneExclusionAsync)] = WhisparrVerbClass.Act,
            [nameof(IWhisparrSceneExclusionActing.RemoveSceneExclusionAsync)] = WhisparrVerbClass.Act,
        };

    /// <summary>Every interface an outbound request of this product can be expressed through.</summary>
    /// <remarks>
    /// Transcribed by hand for the same reason the table above is: a seam interface added later and
    /// left out of this list is what fails
    /// <see cref="SafetyInvariantTests.TheOutboundSeamDeclaresExactlyTheMembersThisProductCanCall"/>,
    /// rather than being covered by an assertion nobody wrote.
    /// </remarks>
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
    ];

    /// <summary>The members doing <paramref name="verbClass"/>'s class of work, in name order.</summary>
    public static IEnumerable<string> MembersOf(WhisparrVerbClass verbClass)
        => VerbClassByMember
            .Where(member => member.Value == verbClass)
            .Select(member => member.Key)
            .Order();

    /// <summary>Every seam interface declaring a member named <paramref name="member"/>.</summary>
    public static IEnumerable<Type> SeamsDeclaring(string member)
        => SeamInterfaces.Where(seam => seam
            .GetMethods()
            .Any(method => string.Equals(method.Name, member, StringComparison.Ordinal)));
}

/// <summary>
/// The safety invariants that are reachable by driving this product, proven over doubles that record
/// the arguments of every request.
/// </summary>
/// <remarks>
/// The invariants concerning a capability this product does not hold are in
/// <see cref="AbsentCapabilityTests"/>, and are asserted as absence rather than as behaviour.
/// </remarks>
public sealed class SafetyInvariantTests
{
    /// <summary>
    /// The one route this extension mounts that answers a caller holding no Cove permission.
    /// </summary>
    /// <remarks>
    /// Written out as a single value rather than a list, so a SECOND anonymous route fails here.
    /// </remarks>
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

    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OneInboundPath)]
    public async Task ExactlyOneMountedRouteAdmitsACallerHoldingNoCovePermission()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWhisparrSyncBindingServices();
        builder.Services.AddRouting();

        await using var app = builder.Build();
        WhisparrSyncFixture.Create().MapEndpoints(app);

        // Route registrations reach the DI EndpointDataSource only when routing middleware is built
        // at start, so without this the source is empty and the assertion holds over nothing.
        await app.StartAsync(TestCt);

        var anonymous = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Where(route => route.Metadata.GetMetadata<CoveAllowAnonymousMetadata>() is not null)
            .Select(route => "/" + route.RoutePattern.RawText?.TrimStart('/'))
            .Order()
            .ToList();

        Assert.Equal([InboundRoute], anonymous);

        await app.StopAsync(TestCt);
    }

    /// <summary>
    /// The transcribed table names every member of every seam interface, and nothing else.
    /// </summary>
    /// <remarks>
    /// One assertion over a declared union rather than one per interface, so a seam interface added
    /// and left out of <see cref="OutboundSeam.SeamInterfaces"/> fails the count below instead of
    /// slipping past an assertion that was never written for it. No name is duplicated across the
    /// union on purpose: two interfaces declaring one name would fail here too.
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingMovedOrDeleted)]
    public void TheOutboundSeamDeclaresExactlyTheMembersThisProductCanCall()
    {
        Assert.Equal(11, OutboundSeam.SeamInterfaces.Count);

        Assert.Equal(
            OutboundSeam.VerbClassByMember.Keys.Order().ToList(),
            OutboundSeam.SeamInterfaces
                .SelectMany(seam => seam.GetMethods())
                .Select(method => method.Name)
                .Order()
                .ToList());
    }

    /// <summary>
    /// Every acting and grabbing member is sent once, and each of those two classes has members.
    /// </summary>
    /// <remarks>
    /// The retry table's silence about both classes, stated as the answer a caller actually gets. A
    /// reader of the table infers the default; this reads it back through
    /// <see cref="WhisparrRetryPolicy.AttemptsFor"/>, which is what a request goes through.
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NoAutoRetriedGrab)]
    public void EveryActingAndGrabbingMemberIsSentOnceAndNeverRetried()
    {
        WhisparrVerbClass[] neverRetried = [WhisparrVerbClass.Act, WhisparrVerbClass.Grab];

        Assert.All(
            neverRetried,
            verbClass => Assert.Equal(
                WhisparrRetryPolicy.NoRetry, WhisparrRetryPolicy.AttemptsFor(verbClass)));

        var members = OutboundSeam.VerbClassByMember
            .Where(member => neverRetried.Contains(member.Value))
            .ToList();

        // Both classes carry members, so neither assertion above is passing over an empty set.
        Assert.Equal(
            neverRetried.Order().ToList(),
            members.Select(member => member.Value).Distinct().Order().ToList());

        Assert.All(
            members,
            member => Assert.Equal(
                WhisparrRetryPolicy.NoRetry, WhisparrRetryPolicy.AttemptsFor(member.Value)));
    }

    /// <summary>
    /// Every add this product can compose carries every acquisition-suppressing spelling its own
    /// resource declares, each present as a member and each false, over every generation, entity kind
    /// and scope the registered capabilities allow.
    /// </summary>
    /// <remarks>
    /// The case list is DERIVED from the per-generation capability table rather than transcribed.
    /// Everywhere else here a transcribed list is the stronger source, because it disagrees with the
    /// code as soon as the code changes and someone has to reconcile the two. The failure this one
    /// has to catch runs the other way: a generation-and-kind combination that becomes registered
    /// and is never covered. A transcribed list would go on agreeing with itself while that
    /// combination composed whatever it liked, so the enumeration reads the same table the product
    /// acts through and a registration with no case fails the suite.
    /// <para>
    /// Presence is asserted apart from the value, because an absent member and a false one read the
    /// same off a value and the instance's default for the absent case is not this product's to
    /// rely on. What each case actually holds is asserted case by case in the body group; this
    /// states the claim and the derivation.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// The only path whose composed body can name a grabbing command is the one reached through the
    /// separately obtained grabbing role, and no monitoring path reaches that role's member.
    /// </summary>
    /// <remarks>
    /// The behavioural half: every body a monitor, unmonitor, scope change or add composes is
    /// searched as serialised text for each transcribed grabbing command name. The type-level half is
    /// the assertion below it, which says the seam declares exactly one member that can grab.
    /// </remarks>
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

        // The member that CAN name one is declared on that role alone, so no monitoring call site
        // holds an implementation to reach it through.
        Assert.Equal(
            [typeof(IWhisparrSearchGrabbing)],
            OutboundSeam.SeamsDeclaring(nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync)));
    }

    /// <summary>
    /// Two members of the whole outbound seam can make an instance download, each declared on a role
    /// of its own that a caller obtains by name.
    /// </summary>
    /// <remarks>
    /// The type-level half of the guarantee: a call site that never obtains one of those roles cannot
    /// express the request, whatever it intended. What a composed body says is asserted above.
    /// <para>
    /// The guarantee is not that nothing can grab. Two named gestures reach one grabbing member
    /// each - one over an entity's whole monitored catalogue, one over a single scene - and nothing
    /// else does. The behavioural half, that every other mounted verb reaches none, is driven over
    /// the mounted route set in the body group.
    /// </para>
    /// <para>
    /// Each grabbing role declares exactly one member, so neither can grow a second verb without
    /// this failing.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// The filesystem seam declares one member and it reads.
    /// </summary>
    /// <remarks>
    /// The structural half of the invariant: whatever a caller intended, there is no member here that
    /// moves, renames, deletes, opens or writes.
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingMovedOrDeleted)]
    public void TheFilesystemSeamDeclaresOnlyTheProbe()
    {
        Assert.Equal(
            [nameof(IImportPathPort.Probe)],
            typeof(IImportPathPort).GetMethods().Select(method => method.Name));
    }

    /// <summary>
    /// A success, each refusal branch and a backstop pass leave only read-class calls behind.
    /// </summary>
    /// <remarks>
    /// The backstop's own reading is asserted first: a pass that walked nowhere would leave an empty
    /// log, and an empty log satisfies every emptiness assertion below it.
    /// </remarks>
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

    /// <summary>
    /// An ingest in which every candidate was refused still sends only read-class calls.
    /// </summary>
    /// <remarks>
    /// The refusals are asserted by cause rather than counted, so a run in which the core answered
    /// the same refusal to everything is not read as three branches.
    /// </remarks>
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

    /// <summary>
    /// Only the read class is listed in the retry table, and a class nobody listed gets one attempt.
    /// </summary>
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

    /// <summary>
    /// Replaying one delivery asks the filesystem seam for nothing but a probe either time, and the
    /// second delivery creates neither an item nor an identity row.
    /// </summary>
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

    /// <summary>A class the retry table does not list.</summary>
    /// <remarks>
    /// Cast from a value the enum does not declare, which is what a class added without a table entry
    /// behaves as.
    /// </remarks>
    private const WhisparrVerbClass UnlistedVerbClass = (WhisparrVerbClass)(-1);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>The whole ingest path, wired over doubles, with one recorder at the outbound seam.</summary>
    /// <remarks>
    /// The reported-root read, the ingest core and the backstop pass are the shipped types rather than
    /// stand-ins, so every request any of them makes leaves through the one recorded client.
    /// </remarks>
    private sealed class Ingest
    {
        public const string ApiKey = "5f5f5f5f5f5f5f5f5f5f5f5f5f5f5f5f";
        public const string RemoteId = "e1a5c0d2-0000-4000-8000-000000000008";
        public const int HeldVideoId = 1;

        /// <summary>A reported file present under exactly one host library root.</summary>
        public const string ReportedPath = WhisparrRoot + "/scene.mp4";

        /// <summary>Where the guard resolves <see cref="ReportedPath"/> to.</summary>
        public const string VerifiedPath = FirstLibraryRoot + "/scene.mp4";

        /// <summary>A reported file the instance declares no root for.</summary>
        public const string PathUnderNoReportedRoot = "/elsewhere/scene.mp4";

        /// <summary>A reported file no candidate for which is on disk.</summary>
        public const string PathOnNoDisk = WhisparrRoot + "/absent.mp4";

        /// <summary>A reported file present under both host library roots.</summary>
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
            new RecordingCredentialPort().Holding(WhisparrGeneration.V3, ApiKey);

        public Ingest()
        {
            Client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "[]"));
            Client.Answering(
                nameof(IWhisparrClient.ReadRootFoldersAsync),
                RecordingWhisparrClient.Json(
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
                Client, _options, _credentials, new ReportedRootCache(clock), NullLogger.Instance);
        }

        public FakeStore Store { get; } = new();

        public RecordingWhisparrClient Client { get; }

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

        /// <summary>The one gate every write in a case goes through, as the container has it.</summary>
        public OptionsWriteGate Gate { get; } = new();

        public Task<BackstopPassResult> BackstopAsync()
            => new BackstopPass(
                    Client,
                    _options,
                    Gate,
                    _credentials,
                    Core(),
                    new FixedClock(Now),
                    _followUp,
                    Library,
                    NullLogger.Instance)
                .RunAsync(TestCt);

        /// <summary>One page holding one import record, naming a file below the reporting root.</summary>
        private static WhisparrResponse HistoryNaming(string tail)
            => RecordingWhisparrClient.Json(
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
                _options,
                Gate,
                _followUp,
                new FixedClock(Now),
                NullLogger.Instance);
    }

    /// <summary>
    /// Nothing under the catalogue derivation can reach the host's stored-value surface at all.
    /// </summary>
    /// <remarks>
    /// The host serialises every stored value on one bulk route, so one oversized value breaks the
    /// whole settings page and survives a reinstall. A type that cannot obtain the store cannot write
    /// a page, a catalogue or a library into it, which is a stronger claim than one about the sizes
    /// it happens to write today.
    /// <para>
    /// The settings blob is written by the options slice, which is not in this set and is bounded by
    /// its own record rather than by anything the library holds.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Every collection a catalogue route answers with is one that was written down.
    /// </summary>
    /// <remarks>
    /// Transcribed, so a collection member added without a decision about what bounds it fails here.
    /// What each is bounded by is asserted by driving the derivation below: a declared type says
    /// nothing about the count an implementation puts in it.
    /// <para>
    /// The facet menus are the one member not bounded by the page. They are bounded by the provider's
    /// own declared menu size, and are read once for the tab rather than per card.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// A page carries at most the number of cards it asked for, and fewer once rows were removed.
    /// </summary>
    /// <remarks>
    /// The page is never topped back up. Fetching more to fill a gap is unbounded where a reader owns
    /// most of an entity, which is the shape this asserts the derivation does not have.
    /// </remarks>
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
            exclusionReading,
            new StubSceneStatusReading(presence: 404));

        Assert.Equal(expected, view.Cards.Count);
        Assert.True(view.Cards.Count <= view.PerPage);
    }

    /// <summary>
    /// The exclusion list is read once for the whole derivation, and never once per card.
    /// </summary>
    /// <remarks>
    /// The instance narrows that route by no parameter, so a read per card would transfer the whole
    /// list forty times to answer forty questions one read already answered.
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingGrowsWithTheLibrary)]
    public async Task TheExclusionListIsReadOncePerDerivationAndNeverOncePerCard()
    {
        var scenes = PageOfScenes(40);
        var exclusionReading = new RecordingExclusionReading([]);

        await DeriveAsync(
            scenes, [], exclusionReading, new StubSceneStatusReading(presence: 404));

        Assert.Equal(1, exclusionReading.Calls);
        var asked = Assert.Single(exclusionReading.AskedAbout);
        Assert.Equal(40, asked.Count);
    }

    /// <summary>
    /// A page's status costs one entity probe plus at most one read per card, and never a read whose
    /// answer grows with what the instance holds.
    /// </summary>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingGrowsWithTheLibrary)]
    public async Task AStatusCostsAtMostOneReadPerCardAndNeverOnePerLibraryRow()
    {
        var scenes = PageOfScenes(40);
        var statusReading = new StubSceneStatusReading();

        var view = await DeriveAsync(
            scenes, [], new RecordingExclusionReading([]), statusReading);

        Assert.Equal(1, statusReading.PresenceReads);
        Assert.True(
            statusReading.SceneReads <= view.Cards.Count,
            $"{statusReading.SceneReads} per-scene reads were spent on {view.Cards.Count} cards.");
    }

    /// <summary>Whether <paramref name="type"/> can obtain <paramref name="reached"/> at all.</summary>
    /// <remarks>
    /// Fields and constructor parameters alike, so a type taking one and not storing it still counts:
    /// it can still hand it on.
    /// </remarks>
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

    // The whole derivation, driven through its real ports rather than a stub of itself, so the counts
    // a case asserts are the counts the derivation actually spent.
    private static Task<MissingPageView> DeriveAsync(
        List<ProviderScene> scenes,
        string[] owned,
        RecordingExclusionReading exclusionReading,
        StubSceneStatusReading statusReading)
    {
        var catalogue = new StubProviderCatalogue(scenes);
        var planner = new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubEntityIdentities("an-entity"), catalogue, new StubEntityNames()),
            catalogue,
            new StubOwnedScenes(owned),
            new SceneStatusPort(),
            new SceneExclusionPort());

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
                new Uri("http://whisparr.invalid:6969"),
                "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e",
                WhisparrGeneration.V3,
                new ResolvedProvider("https://stashdb.org/graphql", "a-key", 240),
                statusReading,
                exclusionReading),
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
    }

    /// <summary>An exclusion role counting how often it was asked and about how many scenes.</summary>
    private sealed class RecordingExclusionReading(string[] excluded) : IWhisparrSceneExclusionReading
    {
        public int Calls { get; private set; }

        public List<IReadOnlyList<string>> AskedAbout { get; } = [];

        public Task<IReadOnlySet<string>> ReduceExclusionsAsync(
            Uri baseAddress,
            string apiKey,
            IReadOnlyCollection<string> providerSceneIds,
            CancellationToken ct)
        {
            Calls++;
            AskedAbout.Add([.. providerSceneIds]);
            return Task.FromResult<IReadOnlySet<string>>(
                providerSceneIds.Where(excluded.Contains).ToHashSet(StringComparer.Ordinal));
        }

        public Task<SceneExclusionLookup> FindSceneExclusionAsync(
            Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
            => throw new NotSupportedException(
                "A page derivation asks about a whole page at once and never for one row's own "
                    + "identifier.");
    }

    /// <summary>The filesystem seam, faked, recording every operation it was asked for.</summary>
    /// <remarks>
    /// The operation is recorded by its member name beside the path, so what the log answers is which
    /// operations a path was subjected to rather than how many times it was touched.
    /// </remarks>
    private sealed class RecordingPathPort : IImportPathPort
    {
        /// <summary>The size of every file this port can find, keyed by path.</summary>
        public Dictionary<string, long> Present { get; } = [];

        /// <summary>Every operation asked of this port, with the path it named.</summary>
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
