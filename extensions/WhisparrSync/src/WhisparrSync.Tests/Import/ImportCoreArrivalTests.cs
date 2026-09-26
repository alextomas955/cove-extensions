using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Linking;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Import;

// A file the instance downloaded into the folder this extension keeps for an entity. The assertions
// are over the path the host import received rather than over the outcome: a core that reported an
// import while handing the host the tree path would satisfy the outcome and none of the claim.
public sealed class ImportCoreArrivalTests
{
    private const string WhisparrRoot = "/whisparr-media";
    private const string CoveRoot = "/data";

    private const string ReportedArrival = WhisparrRoot + "/.wsync-v3/tt1234567/Scene.2026.mp4";
    private const string ArrivalInTheTree = CoveRoot + "/.wsync-v3/tt1234567/Scene.2026.mp4";
    private const string PlacedInTheLibrary = CoveRoot + "/Scene.2026.mp4";

    private const string ReportedOrdinaryPath = WhisparrRoot + "/per-studio/Scene.2026.mp4";
    private const string OrdinaryPath = CoveRoot + "/per-studio/Scene.2026.mp4";

    private const string RemoteId = "e1a5c0d2-0000-4000-8000-000000000004";
    private const long ReportedSize = 10;

    private static readonly FileIdentity TheArrival = new(56, 0x100);
    private static readonly DateTimeOffset Changed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnArrivalIsRegisteredWhereItWasPlacedAndNotWhereTheInstancePutIt()
    {
        var ingest = new Ingest();
        ingest.Links.AnswerLink(PlacedInTheLibrary, LinkOutcome.Linked);

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync());

        Assert.Equal((PlacedInTheLibrary, (int?)null), Assert.Single(ingest.Library.Imported));
        Assert.DoesNotContain(ArrivalInTheTree, ingest.Library.Imported.Select(one => one.Path));
    }

    // The instance goes on recording the arrival where it put it, so the name it knows has to still
    // be there after the placement. Nothing here removes a name.
    [Fact]
    public async Task ThePlacementLeavesTheNameTheInstanceRecordedWhereItIs()
    {
        var ingest = new Ingest();
        ingest.Links.AnswerLink(PlacedInTheLibrary, LinkOutcome.Linked);

        await ingest.DeliverAsync();

        Assert.NotNull(ingest.Links.Identify(ArrivalInTheTree));
        Assert.DoesNotContain("remove", ingest.Links.Calls.Select(call => call.Verb));
    }

    [Fact]
    public async Task ADeliveryOutsideEveryTreeTakesThePathItAlreadyTookAndReachesNoLinking()
    {
        var ingest = new Ingest();

        Assert.Equal(
            ImportOutcome.Imported,
            await ingest.DeliverAsync(reportedPath: ReportedOrdinaryPath));

        Assert.Equal((OrdinaryPath, (int?)null), Assert.Single(ingest.Library.Imported));
        Assert.Empty(ingest.Links.Calls);
    }

    // The second of the two ingest channels to reach one arrival. The name is there and holds the
    // arrival's own bytes, so the delivery settles as already held rather than registering a second
    // item or reporting a refusal.
    [Fact]
    public async Task ASecondDeliveryOfOneArrivalRegistersNothingAndIsNotRefused()
    {
        var ingest = new Ingest();
        ingest.Links.AnswerLink(PlacedInTheLibrary, LinkOutcome.Linked);
        await ingest.DeliverAsync();
        ingest.Holds(PlacedInTheLibrary);

        Assert.Equal(ImportOutcome.AlreadyHeld, await ingest.DeliverAsync());

        Assert.Single(ingest.Library.Imported);
    }

    [Fact]
    public async Task AnArrivalThatCannotBePlacedIsRefusedUnderItsOwnCauseAndRegistersNothing()
    {
        var ingest = new Ingest();
        ingest.Links.AnswerLink(PlacedInTheLibrary, LinkOutcome.Refused);

        Assert.Equal(ImportOutcome.RefusedArrivalNotPlaced, await ingest.DeliverAsync());

        Assert.Empty(ingest.Library.Imported);
        var refused = Assert.Single((await ingest.StoredAsync()).Instance().ImportRefusals);
        Assert.Equal(WhisparrRoot, refused.Root);
        Assert.Equal(
            ImportRefusalCause.NotPlacedInLibrary, Assert.Single(refused.NewestPaths).Cause);
    }

    // The downloaded file is the reader's only copy of what they waited for, and no later pass will
    // remove it: removal is confined to names this extension composed, and this is not one.
    [Fact]
    public async Task ARefusedPlacementLeavesTheDownloadedFileAlone()
    {
        var ingest = new Ingest();
        ingest.Links.AnswerLink(PlacedInTheLibrary, LinkOutcome.Refused);

        await ingest.DeliverAsync();

        Assert.NotNull(ingest.Links.Identify(ArrivalInTheTree));
        Assert.DoesNotContain("remove", ingest.Links.Calls.Select(call => call.Verb));
    }

    private sealed class Ingest
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

        public Ingest() => Links.Place(ArrivalInTheTree, TheArrival, Changed);

        public FakeStore Store { get; } = new();

        public RecordingLibrary Library { get; } = new(reached: true, [CoveRoot]);

        public RecordingTreeLinkPort Links { get; } = new();

        public StubPaths Paths { get; } = new()
        {
            Present = { [ArrivalInTheTree] = ReportedSize, [OrdinaryPath] = ReportedSize },
        };

        public void Holds(string path) => Library.Held[path] = new HeldFile(1);

        public Task<WhisparrSyncOptions> StoredAsync()
            => new OptionsStore(Store).LoadAsync(TestContext.Current.CancellationToken);

        public Task<ImportOutcome> DeliverAsync(string reportedPath = ReportedArrival)
            => new ImportCore(
                    new StubReportedRoots(WhisparrRoot),
                    Library,
                    new ImportFilesystem(Paths, Links),
                    new OptionsWriting(new OptionsStore(Store), new OptionsWriteGate()),
                    new FollowUpScanCoalescer(TimeProvider.System, NullLogger.Instance),
                    new FixedClock(Now),
                    NullLogger.Instance)
                .IngestAsync(
                    new ImportCandidate(
                        WhisparrGeneration.V3, "Download", reportedPath, ReportedSize, RemoteId),
                    TestContext.Current.CancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubReportedRoots(params string[] roots) : IReportedRootPort
    {
        public Task<IReadOnlyList<string>?> ReadAsync(
            WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>?>(roots);
    }

    private sealed class StubPaths : IImportPathPort
    {
        public Dictionary<string, long> Present { get; } = [];

        public ProbedPath Probe(string path)
            => Present.TryGetValue(path, out var size)
                ? new ProbedPath(true, size)
                : new ProbedPath(false, null);
    }
}
