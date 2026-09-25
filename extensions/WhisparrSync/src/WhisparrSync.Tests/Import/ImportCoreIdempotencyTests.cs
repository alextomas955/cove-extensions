using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Import;

// The assertions are over the arguments and the call counts the host seams saw, not over the
// returned outcome alone: a core that reported a done-already outcome while still calling the host
// import would satisfy the outcome and none of the claim.
public sealed class ImportCoreIdempotencyTests
{
    private const string WhisparrRoot = "/whisparr-media";

    // A second root, whose line is the control that tells a per-root clear from a global one.
    private const string OtherWhisparrRoot = "/whisparr-elsewhere";

    private const string ReportedPath = "/whisparr-media/scene.mp4";
    private const string VerifiedPath = "/data/scene.mp4";

    // No probe finds this path, so a delivery naming it is refused by name.
    private const string MissingPath = "/whisparr-media/nothing-is-here.mp4";

    private const string RemoteId = "e1a5c0d2-0000-4000-8000-000000000004";
    private const long ReportedSize = 10;

    [Fact]
    public async Task APathTheLibraryDoesNotHoldGoesThroughTheWholeSequence()
    {
        var ingest = new Ingest();

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync());

        Assert.Equal((VerifiedPath, (int?)null), Assert.Single(ingest.Library.Imported));
        Assert.Single(ingest.Library.Stamped);
        Assert.Single(ingest.Library.Enriched);
    }

    [Fact]
    public async Task ASecondDeliveryOfOnePathReachesNoHostImportAndNoEnrichment()
    {
        var ingest = new Ingest();
        await ingest.DeliverAsync();
        ingest.Holds(VerifiedPath);

        var outcome = await ingest.DeliverAsync();

        // The seams first, then the outcome: a core that reported a done-already outcome while still
        // calling the host import would pass an outcome-first assertion for the wrong reason.
        Assert.Single(ingest.Library.Imported);
        Assert.Single(ingest.Library.Stamped);
        Assert.Single(ingest.Library.Enriched);
        Assert.Equal(ImportOutcome.AlreadyHeld, outcome);
    }

    [Fact]
    public async Task ABackstopCandidateForAPathTheLiveChannelImportedDoesNoWork()
    {
        var ingest = new Ingest();
        await ingest.DeliverAsync();
        ingest.Holds(VerifiedPath);

        Assert.Equal(ImportOutcome.AlreadyHeld, await ingest.DeliverAsync(remoteId: null));

        Assert.Single(ingest.Library.Imported);
        Assert.Single(ingest.Library.Stamped);
        Assert.Single(ingest.Library.Enriched);
    }

    [Fact]
    public async Task TheLiveChannelArrivingSecondStampsTheIdentityTheBackstopCouldNotRead()
    {
        var ingest = new Ingest();
        await ingest.DeliverAsync(remoteId: null);
        ingest.Holds(VerifiedPath);

        Assert.Equal(ImportOutcome.AlreadyHeld, await ingest.DeliverAsync());

        Assert.Single(ingest.Library.Imported);
        Assert.Equal(
            (1, "https://stashdb.org/graphql", RemoteId), Assert.Single(ingest.Library.Stamped));
    }

    // Handing the host a path it already has a row for and no item to attach it to is the one input
    // its import answers by raising, so this is the refusal that keeps the delivery inside its answer.
    [Fact]
    public async Task ADeliveryForADetachedPathWithNoIdentityReachesNoHostImport()
    {
        var ingest = new Ingest();
        ingest.HoldsDetached(VerifiedPath);

        Assert.Equal(
            ImportOutcome.RefusedDetachedFileWithoutIdentity,
            await ingest.DeliverAsync(remoteId: null));

        Assert.Empty(ingest.Library.Imported);
        Assert.Empty(ingest.Library.Stamped);
        Assert.Empty(ingest.Library.Enriched);
    }

    // Asserted on the argument the host import received. A core that called it with no key would
    // satisfy "the host was called" and none of the claim: that key is the difference between the host
    // re-attaching the row and the host raising.
    [Fact]
    public async Task ADeliveryForADetachedPathWithAnIdentityHandsTheHostThatVideoKey()
    {
        const int carrier = 7;
        var ingest = new Ingest();
        ingest.HoldsDetached(VerifiedPath);
        ingest.Library.ExistingIdentities.Add((carrier, "https://stashdb.org/graphql", RemoteId));

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync());

        Assert.Equal((VerifiedPath, (int?)carrier), Assert.Single(ingest.Library.Imported));
    }

    [Fact]
    public async Task TheDedupeReadIsIssuedOnEveryDeliveryIncludingTheSecond()
    {
        var ingest = new Ingest();
        await ingest.DeliverAsync();
        ingest.Holds(VerifiedPath);
        await ingest.DeliverAsync();
        await ingest.DeliverAsync();

        Assert.Equal([VerifiedPath, VerifiedPath, VerifiedPath], ingest.Library.Probed);
    }

    // The seeded line belongs to another root, which this delivery neither clears nor touches, so the
    // fold answers a value equal to the stored one. The blob is compared as the store holds it, so a
    // save that wrote an equal value would still be caught by the write count beside it.
    [Fact]
    public async Task ADeliveryThatRegisteredNothingLeavesTheStoredBlobByteIdentical()
    {
        var ingest = new Ingest();
        await ingest.SeedRefusalAsync(OtherWhisparrRoot);
        ingest.Holds(VerifiedPath);

        var before = await ingest.Store.GetAllAsync(TestContext.Current.CancellationToken);
        var writes = ingest.Store.SetCallCount;

        Assert.Equal(ImportOutcome.AlreadyHeld, await ingest.DeliverAsync());

        Assert.Equal(
            before, await ingest.Store.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(writes, ingest.Store.SetCallCount);
    }

    // This is the ordinary recovery path. The user adds the root they were missing, Cove's own scan
    // imports the files, and the next delivery finds them already held. If this branch reported
    // nothing, the banner would keep naming a root the user had already fixed until a genuinely new
    // file arrived under it.
    [Fact]
    public async Task AnAlreadyHeldDeliveryCoversItsPathAndClearsOnlyItsOwnRootsLine()
    {
        var ingest = new Ingest();
        await ingest.SeedRefusalAsync();
        await ingest.SeedRefusalAsync(OtherWhisparrRoot);
        ingest.Holds(VerifiedPath);

        Assert.Equal(ImportOutcome.AlreadyHeld, await ingest.DeliverAsync());

        ingest.FollowUp.Flush(ingest.Library);
        Assert.Equal([VerifiedPath], Assert.Single(ingest.Library.Scans));
        Assert.Equal(
            OtherWhisparrRoot,
            Assert.Single((await ingest.StoredAsync()).Instance().ImportRefusals).Root);
    }

    [Fact]
    public async Task AnAlreadyHeldDeliveryRecordsNoImportAsHavingWorked()
    {
        var ingest = new Ingest();
        await ingest.SeedRefusalAsync();
        ingest.Holds(VerifiedPath);

        Assert.Equal(ImportOutcome.AlreadyHeld, await ingest.DeliverAsync());

        Assert.Null((await ingest.StoredAsync()).ImportHealth.LastWorkedAtUtc);
        Assert.Empty(ingest.Library.Imported);
    }

    // Written from the ingest rather than from the backstop, because the live channel imports with no
    // pass running at all and a member only a pass wrote would read as never against a working webhook.
    [Fact]
    public async Task AnIngestThatRegisteredAFileRecordsWhenAnImportLastWorked()
    {
        var ingest = new Ingest();

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync());

        Assert.Equal(Ingest.Now, (await ingest.StoredAsync()).ImportHealth.LastWorkedAtUtc);
    }

    [Fact]
    public async Task AnIngestThatWasRefusedRecordsNoImportAsHavingWorked()
    {
        var ingest = new Ingest();

        Assert.Equal(
            ImportOutcome.RefusedNotFound, await ingest.DeliverAsync(reportedPath: MissingPath));

        Assert.Null((await ingest.StoredAsync()).ImportHealth.LastWorkedAtUtc);
    }

    private sealed class Ingest
    {
        public static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

        public FakeStore Store { get; } = new();

        public OptionsWriteGate Gate { get; } = new();

        public RecordingLibrary Library { get; } = new(reached: true, ["/data"]);

        public StubPaths Paths { get; } = new() { Present = { [VerifiedPath] = ReportedSize } };

        public void Holds(string path) => Library.Held[path] = new HeldFile(1);

        public void HoldsDetached(string path) => Library.Held[path] = new HeldFile(null);

        public FollowUpScanCoalescer FollowUp { get; } = new(new FixedClock(Now), NullLogger.Instance);

        // One outstanding refusal in the blob, so a clearing write would show.
        public async Task SeedRefusalAsync(string root = WhisparrRoot)
        {
            var options = new OptionsStore(Store);
            var stored = await options.LoadAsync(TestContext.Current.CancellationToken);
            await options.SaveAsync(
                stored.WithInstance(
                    importRefusals: ImportRefusalProjector.Refuse(
                        stored.Instance().ImportRefusals,
                        root,
                        ReportedPath,
                        ImportRefusalCause.NotFoundUnderAnyRoot)),
                TestContext.Current.CancellationToken);
        }

        public Task<WhisparrSyncOptions> StoredAsync()
            => new OptionsStore(Store).LoadAsync(TestContext.Current.CancellationToken);

        public Task<ImportOutcome> DeliverAsync(
            string? remoteId = RemoteId, string reportedPath = ReportedPath)
            => new ImportCore(
                    new StubReportedRoots(WhisparrRoot),
                    Library,
                    Paths,
                    new OptionsWriting(new OptionsStore(Store), Gate),
                    FollowUp,
                    new FixedClock(Now),
                    NullLogger.Instance)
                .IngestAsync(
                    new ImportCandidate(
                        WhisparrGeneration.V3, "Download", reportedPath, ReportedSize, remoteId),
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
