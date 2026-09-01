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
using WhisparrSync.Options;
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

    /// <summary>Every invariant this product declares.</summary>
    public static string[] All =>
    [
        OneInboundPath,
        NothingMovedOrDeleted,
        NoAutoRetriedGrab,
        EveryAddIsNonGrabbing,
        OnlyAnExplicitSearchGrabs,
        EveryMutationIsOriginTagged,
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
            [nameof(IWhisparrClient.ReadHistoryAsync)] = WhisparrVerbClass.Read,
            [nameof(IWhisparrClient.CreateNotificationAsync)] = WhisparrVerbClass.Configure,
            [nameof(IWhisparrClient.UpdateNotificationAsync)] = WhisparrVerbClass.Configure,
        };

    /// <summary>The members doing <paramref name="verbClass"/>'s class of work, in name order.</summary>
    public static IEnumerable<string> MembersOf(WhisparrVerbClass verbClass)
        => VerbClassByMember
            .Where(member => member.Value == verbClass)
            .Select(member => member.Key)
            .Order();
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

    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.NothingMovedOrDeleted)]
    public void TheOutboundSeamDeclaresExactlyTheMembersThisProductCanCall()
    {
        Assert.Equal(
            OutboundSeam.VerbClassByMember.Keys.Order().ToList(),
            typeof(IWhisparrClient).GetMethods().Select(method => method.Name).Order().ToList());
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
