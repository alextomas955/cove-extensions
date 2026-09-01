using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// What one delivery does about identity: which row is written, when the source is asked for a
/// record, and what happens when asking fails.
/// </summary>
public sealed class ImportCoreIdentityTests
{
    private const string WhisparrRoot = "/whisparr-media";
    private const string ReportedPath = "/whisparr-media/scene.mp4";
    private const string VerifiedPath = "/data/scene.mp4";
    private const string RemoteId = "e1a5c0d2-0000-4000-8000-000000000003";
    private const long ReportedSize = 10;

    /// <summary>
    /// Transcribed by hand from the sources this product identifies against, per generation. The
    /// stamp falls back to these when the host is configured with no source, which is its default.
    /// </summary>
    [Theory]
    [InlineData(WhisparrGeneration.V3, "https://stashdb.org/graphql")]
    [InlineData(WhisparrGeneration.V2, "https://theporndb.net/graphql")]
    public async Task WithNoConfiguredSourceTheStampFallsBackToTheProvidersStandardAddress(
        WhisparrGeneration generation, string expected)
    {
        var ingest = new Ingest();

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync(generation: generation));

        Assert.Equal((1, expected, RemoteId), Assert.Single(ingest.Library.Stamped));
    }

    /// <summary>
    /// A host configured at another spelling of the same source gets that spelling, so the host's own
    /// merge - which dedupes those rows by exact string - finds the row rather than adding a second.
    /// </summary>
    [Fact]
    public async Task TheStampTakesTheHostsOwnSpellingOfTheSourceWhenItIsConfiguredWithOne()
    {
        var ingest = new Ingest();
        ingest.Library.ConfiguredEndpoints.Add("https://api.stashdb.org/graphql/");

        await ingest.DeliverAsync();

        Assert.Equal(
            (1, "https://api.stashdb.org/graphql/", RemoteId), Assert.Single(ingest.Library.Stamped));
    }

    [Fact]
    public async Task AFirstDeliveryStampsTheRowAndAsksTheSourceForARecordExactlyOnce()
    {
        var ingest = new Ingest();

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync());

        Assert.Single(ingest.Library.Stamped);
        Assert.Equal((1, "https://stashdb.org/graphql", RemoteId), Assert.Single(ingest.Library.Enriched));
    }

    [Fact]
    public async Task ARedeliveryOfTheSameSceneStampsNothingAndAsksTheSourceForNothing()
    {
        var ingest = new Ingest();
        await ingest.DeliverAsync();
        ingest.Library.Held[VerifiedPath] = new HeldFile(1);

        Assert.Equal(ImportOutcome.AlreadyHeld, await ingest.DeliverAsync());

        Assert.Single(ingest.Library.Stamped);
        Assert.Single(ingest.Library.Enriched);
    }

    /// <summary>
    /// A scene the library already identified before this product ever saw it. Nothing is stamped and
    /// the source is not asked, which is what keeps a user's own edits.
    /// </summary>
    [Fact]
    public async Task ASceneTheLibraryAlreadyIdentifiedIsNeitherStampedNorEnriched()
    {
        var ingest = new Ingest();
        ingest.Library.ExistingIdentities.Add((7, "https://api.stashdb.org/graphql", RemoteId));

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync());

        Assert.Empty(ingest.Library.Stamped);
        Assert.Empty(ingest.Library.Enriched);
        Assert.Equal((VerifiedPath, (int?)7), Assert.Single(ingest.Library.Imported));
    }

    [Fact]
    public async Task ADeliveryCarryingNoIdentifierStampsNothingEnrichesNothingAndStillCreatesTheItem()
    {
        var ingest = new Ingest();

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync(remoteId: null));

        Assert.Empty(ingest.Library.Stamped);
        Assert.Empty(ingest.Library.Enriched);
        Assert.Equal((VerifiedPath, (int?)null), Assert.Single(ingest.Library.Imported));
    }

    [Fact]
    public async Task AnIdentifierTwoItemsCarryIsRefusedAndReachesNoHostImport()
    {
        var ingest = new Ingest();
        ingest.Library.IdentityIsAmbiguous = true;

        Assert.Equal(ImportOutcome.RefusedAmbiguousIdentity, await ingest.DeliverAsync());

        Assert.Empty(ingest.Library.Imported);
        Assert.Empty(ingest.Library.Stamped);
        Assert.Empty(ingest.Library.Enriched);
    }

    /// <summary>The documented failure of an unconfigured source, contained.</summary>
    [Fact]
    public async Task AnUnconfiguredSourceIsCaughtLoggedOnceAndTheImportStillSucceeds()
    {
        var log = new CountingLogger();
        var ingest = new Ingest(log);
        ingest.Library.EnrichmentFailure =
            new InvalidOperationException("Configured metadata-server endpoint not found");

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync());

        Assert.Single(ingest.Library.Stamped);
        Assert.Equal(1, log.ContainedEnrichments);
    }

    /// <summary>
    /// The same seam raising cancellation must NOT be contained, which holds only while the
    /// cancellation catch sits above the broad one.
    /// </summary>
    [Fact]
    public async Task ACancellationFromTheSameSeamPropagatesRatherThanBeingContained()
    {
        var log = new CountingLogger();
        var ingest = new Ingest(log);
        ingest.Library.EnrichmentFailure = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ingest.DeliverAsync());

        Assert.Equal(0, log.ContainedEnrichments);
    }

    /// <summary>One ingest wired over fakes, with the identity seams recorded.</summary>
    private sealed class Ingest(ILogger? log = null)
    {
        public FakeStore Store { get; } = new();

        public RecordingLibrary Library { get; } = new(reached: true, ["/data"]);

        public StubPaths Paths { get; } = new() { Present = { [VerifiedPath] = ReportedSize } };

        public Task<ImportOutcome> DeliverAsync(
            WhisparrGeneration generation = WhisparrGeneration.V3,
            string? remoteId = RemoteId)
            => new ImportCore(
                    new StubReportedRoots(WhisparrRoot),
                    Library,
                    Paths,
                    new OptionsStore(Store),
                    new FollowUpScanCoalescer(TimeProvider.System, NullLogger.Instance),
                    log ?? NullLogger.Instance)
                .IngestAsync(
                    new ImportCandidate(
                        generation, "Download", ReportedPath, ReportedSize, remoteId),
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

    /// <summary>Counts the one contained-enrichment line, by its event id.</summary>
    private sealed class CountingLogger : ILogger
    {
        private const int ContainedEnrichmentEventId = 2106;

        public int ContainedEnrichments { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == ContainedEnrichmentEventId)
            {
                ContainedEnrichments++;
            }
        }
    }
}
