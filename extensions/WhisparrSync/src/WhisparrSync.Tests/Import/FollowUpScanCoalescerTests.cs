using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// Every import covered by a follow-up scan, without one exclusive library scan per imported file.
/// </summary>
/// <remarks>
/// The assertions are over the PATHS each scan was asked to cover, not over how many scans ran: a
/// coalescer that started one scan over one path would satisfy a count and cover nine files with
/// nothing.
/// </remarks>
public sealed class FollowUpScanCoalescerTests
{
    private const string WhisparrRoot = "/whisparr-media";
    private const long ReportedSize = 10;

    private static readonly DateTimeOffset Start = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TenImportsInsideOneQuietPeriodStartOneScanCoveringAllTenPaths()
    {
        var ingest = new Ingest();
        var expected = new List<string>();

        for (var scene = 0; scene < 10; scene++)
        {
            var delivered = await ingest.DeliverAsync(scene);
            Assert.Equal(ImportOutcome.Imported, delivered.Outcome);
            expected.Add(delivered.Path);
            ingest.Clock.Advance(TimeSpan.FromMilliseconds(200));
        }

        ingest.Clock.Advance(FollowUpScanCoalescer.QuietPeriod);
        ingest.FollowUp.FlushIfQuiet(ingest.Library);

        Assert.Equal(expected.Order(), Assert.Single(ingest.Library.Scans).Order());
    }

    /// <summary>The discriminating control: a scan covers a path only because an import put it there.</summary>
    [Fact]
    public async Task AnIngestThatRegisteredNothingStartsNoScan()
    {
        var ingest = new Ingest();

        // Nothing on disk under any library root, so the candidate resolves to no file at all.
        Assert.Equal(ImportOutcome.RefusedNotFound, await ingest.DeliverMissingAsync());

        ingest.Clock.Advance(FollowUpScanCoalescer.QuietPeriod);
        ingest.FollowUp.FlushIfQuiet(ingest.Library);
        ingest.FollowUp.Flush(ingest.Library);

        Assert.Empty(ingest.Library.Scans);
    }

    /// <summary>
    /// A delivery the library already holds a file for is still covered by a follow-up, and is still
    /// not counted as an import.
    /// </summary>
    /// <remarks>
    /// The cover is what closes the gap where a delivery registered the file with the host and was
    /// interrupted before it could note the path, which would otherwise leave that item with no
    /// asset-generation pass for good.
    /// </remarks>
    [Fact]
    public async Task ADeliveryForAPathTheLibraryAlreadyHoldsIsStillCoveredByAScan()
    {
        var ingest = new Ingest();
        var first = await ingest.DeliverAsync(0);
        ingest.Library.Held[first.Path] = new HeldFile(1);
        ingest.FollowUp.Flush(ingest.Library);
        ingest.Library.Scans.Clear();

        Assert.Equal(ImportOutcome.AlreadyHeld, (await ingest.DeliverAsync(0)).Outcome);

        ingest.FollowUp.Flush(ingest.Library);
        Assert.Equal([first.Path], Assert.Single(ingest.Library.Scans));
    }

    [Fact]
    public async Task ABatchIsNotStartedUntilItsQuietPeriodHasElapsed()
    {
        var ingest = new Ingest();
        await ingest.DeliverAsync(0);

        ingest.Clock.Advance(FollowUpScanCoalescer.QuietPeriod - TimeSpan.FromMilliseconds(1));
        ingest.FollowUp.FlushIfQuiet(ingest.Library);
        Assert.Empty(ingest.Library.Scans);

        ingest.Clock.Advance(TimeSpan.FromMilliseconds(1));
        ingest.FollowUp.FlushIfQuiet(ingest.Library);
        Assert.Single(ingest.Library.Scans);
    }

    /// <summary>
    /// A further import restarts the quiet period, and the ceiling is what stops that going on for
    /// ever: a burst that never falls quiet is still bounded in both what it holds and how long it
    /// waits.
    /// </summary>
    [Fact]
    public void ABatchReachingItsCeilingIsStartedWithoutWaitingToFallQuiet()
    {
        var library = new RecordingLibrary(reached: true, ["/data"]);
        var clock = new MovableClock(Start);
        var followUp = new FollowUpScanCoalescer(clock, NullLogger.Instance);

        for (var file = 0; file < FollowUpScanCoalescer.PendingCeiling; file++)
        {
            followUp.NoteImported(
                string.Create(CultureInfo.InvariantCulture, $"/data/{file}.mp4"), library);
        }

        Assert.Equal(FollowUpScanCoalescer.PendingCeiling, Assert.Single(library.Scans).Count);

        // The batch was taken rather than copied: a flush straight afterwards has nothing left.
        followUp.Flush(library);
        Assert.Single(library.Scans);
    }

    /// <summary>
    /// A path noted after a flush has begun is in the next batch rather than in none: the take and
    /// the note cannot interleave, so no import is left uncovered by arriving as a window closed.
    /// </summary>
    [Fact]
    public void APathNotedAfterAFlushIsCoveredByTheNextOne()
    {
        var library = new RecordingLibrary(reached: true, ["/data"]);
        var clock = new MovableClock(Start);
        var followUp = new FollowUpScanCoalescer(clock, NullLogger.Instance);

        followUp.NoteImported("/data/first.mp4", library);
        followUp.Flush(library);
        followUp.NoteImported("/data/second.mp4", library);
        followUp.Flush(library);

        Assert.Equal(["/data/first.mp4"], library.Scans[0]);
        Assert.Equal(["/data/second.mp4"], library.Scans[1]);
        Assert.Equal(2, library.Scans.Count);
    }

    [Fact]
    public void APendingBatchIsDroppedRatherThanFlushedAndTheDropIsReportedOnce()
    {
        var library = new RecordingLibrary(reached: true, ["/data"]);
        var log = new CountingLogger();
        var followUp = new FollowUpScanCoalescer(new MovableClock(Start), log);

        followUp.NoteImported("/data/first.mp4", library);
        followUp.NoteImported("/data/second.mp4", library);
        followUp.Drop();
        followUp.Flush(library);

        Assert.Empty(library.Scans);
        Assert.Equal(1, log.Drops);
    }

    /// <summary>Nothing to drop is nothing to report.</summary>
    [Fact]
    public void AShutdownWithNothingPendingReportsNoDrop()
    {
        var log = new CountingLogger();
        new FollowUpScanCoalescer(new MovableClock(Start), log).Drop();

        Assert.Equal(0, log.Drops);
    }

    /// <summary>
    /// The pending batch never reaches the store, so the one blob the host's bulk data route serves
    /// whole is the same size after a burst as after a single import.
    /// </summary>
    /// <remarks>
    /// Compared against the blob after the FIRST import rather than against the one before it: an
    /// import records that the channel worked, which is a fixed-size instant. What must not grow with
    /// the burst is everything else.
    /// </remarks>
    [Fact]
    public async Task ABurstOfImportsLeavesTheStoredBlobByteIdenticalToOneImports()
    {
        var ingest = new Ingest();
        await ingest.SeedAsync();

        await ingest.DeliverAsync(0);
        var afterOne = await ingest.Store.GetAllAsync(TestContext.Current.CancellationToken);

        for (var scene = 1; scene < 10; scene++)
        {
            await ingest.DeliverAsync(scene);
        }

        ingest.Clock.Advance(FollowUpScanCoalescer.QuietPeriod);
        ingest.FollowUp.FlushIfQuiet(ingest.Library);

        Assert.Single(ingest.Library.Scans);
        Assert.Equal(afterOne, await ingest.Store.GetAllAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A batch with nowhere to go is reported rather than swallowed.</summary>
    [Fact]
    public void ABatchTheHostsScanServiceCannotTakeIsReportedOnce()
    {
        var library = new RecordingLibrary(reached: true, ["/data"]) { FollowUpScanIsReachable = false };
        var log = new CountingLogger();
        var followUp = new FollowUpScanCoalescer(new MovableClock(Start), log);

        followUp.NoteImported("/data/first.mp4", library);
        followUp.Flush(library);

        Assert.Empty(library.Scans);
        Assert.Equal(1, log.Unavailable);
    }

    /// <summary>One ingest over fakes, with a clock a test moves and the scan seam recorded.</summary>
    private sealed class Ingest
    {
        public FakeStore Store { get; } = new();

        public MovableClock Clock { get; } = new(Start);

        public RecordingLibrary Library { get; } = new(reached: true, ["/data"]);

        public StubPaths Paths { get; } = new();

        public FollowUpScanCoalescer FollowUp => _followUp ??= new(Clock, NullLogger.Instance);

        private FollowUpScanCoalescer? _followUp;

        public Task SeedAsync()
            => new OptionsStore(Store).SaveAsync(
                new WhisparrSyncOptions { CallbackHost = "http://cove:5073" },
                TestContext.Current.CancellationToken);

        /// <summary>Delivers one scene, answering with the path the guard verified and the outcome.</summary>
        public async Task<(string Path, ImportOutcome Outcome)> DeliverAsync(int scene)
        {
            var reported = string.Create(CultureInfo.InvariantCulture, $"{WhisparrRoot}/{scene}.mp4");
            var verified = string.Create(CultureInfo.InvariantCulture, $"/data/{scene}.mp4");
            Paths.Present[verified] = ReportedSize;

            return (verified, await IngestAsync(reported));
        }

        /// <summary>Delivers one scene whose file is under no library root at all.</summary>
        public Task<ImportOutcome> DeliverMissingAsync() => IngestAsync(WhisparrRoot + "/absent.mp4");

        private Task<ImportOutcome> IngestAsync(string reportedPath)
            => new ImportCore(
                    new StubReportedRoots(WhisparrRoot),
                    Library,
                    Paths,
                    new OptionsStore(Store),
                    new OptionsWriteGate(),
                    FollowUp,
                    Clock,
                    NullLogger.Instance)
                .IngestAsync(
                    new ImportCandidate(
                        WhisparrGeneration.V3, "Download", reportedPath, ReportedSize, null),
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

    /// <summary>A clock a test moves by hand. No timers: nothing here waits on one.</summary>
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Counts the two follow-up lines, by their event ids.</summary>
    private sealed class CountingLogger : ILogger
    {
        private const int BatchDroppedEventId = 2109;
        private const int ScanUnavailableEventId = 2110;

        public int Drops { get; private set; }

        public int Unavailable { get; private set; }

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
            if (eventId.Id == BatchDroppedEventId)
            {
                Drops++;
            }

            if (eventId.Id == ScanUnavailableEventId)
            {
                Unavailable++;
            }
        }
    }
}
