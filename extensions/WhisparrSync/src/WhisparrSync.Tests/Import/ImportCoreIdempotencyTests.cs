using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// The two channels ingesting one file exactly once between them, derived live on every delivery
/// rather than remembered.
/// </summary>
/// <remarks>
/// The assertions are over the ARGUMENTS and the call counts the host seams saw, not over the
/// returned outcome alone: a core that reported a done-already outcome while still calling the host
/// import would satisfy the outcome and none of the claim.
/// </remarks>
public sealed class ImportCoreIdempotencyTests
{
    private const string WhisparrRoot = "/whisparr-media";
    private const string ReportedPath = "/whisparr-media/scene.mp4";
    private const string VerifiedPath = "/data/scene.mp4";
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

    /// <summary>
    /// The backstop projects no identifier off a history record, so the pass that arrives second at a
    /// path the live channel already brought in reaches nothing at all.
    /// </summary>
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

    /// <summary>
    /// The live channel arriving second at a path the backstop brought in: the import is still not
    /// repeated, and the identity the backstop could not read is written now.
    /// </summary>
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

    /// <summary>
    /// A row no item claims, named by a delivery carrying no identifier, reaches the host not at all.
    /// </summary>
    /// <remarks>
    /// Handing the host a path it already has a row for and no item to attach it to is the one input
    /// its import answers by raising, so this is the refusal that keeps the delivery inside its
    /// answer.
    /// </remarks>
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

    /// <summary>
    /// The same row, named by a delivery whose identifier resolves, is handed to the host WITH that
    /// item's key.
    /// </summary>
    /// <remarks>
    /// Asserted on the argument the host import received. A core that called it with no key would
    /// satisfy "the host was called" and none of the claim - that key is the whole difference between
    /// the host re-attaching the row and the host raising.
    /// </remarks>
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

    /// <summary>
    /// The blob is compared as the store holds it, so a save that wrote an equal value would still be
    /// caught by the write count beside it.
    /// </summary>
    [Fact]
    public async Task ADeliveryThatRegisteredNothingLeavesTheStoredBlobByteIdentical()
    {
        var ingest = new Ingest();
        await ingest.SeedRefusalAsync();
        ingest.Holds(VerifiedPath);

        var before = await ingest.Store.GetAllAsync(TestContext.Current.CancellationToken);
        var writes = ingest.Store.SetCallCount;

        Assert.Equal(ImportOutcome.AlreadyHeld, await ingest.DeliverAsync());

        Assert.Equal(
            before, await ingest.Store.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(writes, ingest.Store.SetCallCount);
    }

    /// <summary>One ingest wired over fakes, with the live dedupe read recorded.</summary>
    private sealed class Ingest
    {
        public FakeStore Store { get; } = new();

        public RecordingLibrary Library { get; } = new(reached: true, ["/data"]);

        public StubPaths Paths { get; } = new() { Present = { [VerifiedPath] = ReportedSize } };

        public void Holds(string path) => Library.Held[path] = new HeldFile(1);

        /// <summary>Puts a row at <paramref name="path"/> that no item claims.</summary>
        public void HoldsDetached(string path) => Library.Held[path] = new HeldFile(null);

        /// <summary>Puts one outstanding refusal in the blob, so a clearing write would show.</summary>
        public async Task SeedRefusalAsync()
        {
            var options = new OptionsStore(Store);
            var stored = await options.LoadAsync(TestContext.Current.CancellationToken);
            await options.SaveAsync(
                stored with
                {
                    ImportRefusals = ImportRefusalProjector.Refuse(
                        stored.ImportRefusals,
                        WhisparrRoot,
                        ReportedPath,
                        ImportRefusalCause.NotFoundUnderAnyRoot),
                },
                TestContext.Current.CancellationToken);
        }

        public Task<ImportOutcome> DeliverAsync(string? remoteId = RemoteId)
            => new ImportCore(
                    new StubReportedRoots(WhisparrRoot),
                    Library,
                    Paths,
                    new OptionsStore(Store),
                    new FollowUpScanCoalescer(TimeProvider.System, NullLogger.Instance),
                    NullLogger.Instance)
                .IngestAsync(
                    new ImportCandidate(
                        WhisparrGeneration.V3, "Download", ReportedPath, ReportedSize, remoteId),
                    TestContext.Current.CancellationToken);
    }

    private sealed class StubReportedRoots(params string[] roots) : IReportedRootPort
    {
        public Task<IReadOnlyList<string>> ReadAsync(
            WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(roots);
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
