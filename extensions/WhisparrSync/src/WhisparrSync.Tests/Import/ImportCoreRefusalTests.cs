using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// What the ingest does with each of the three resolutions: which reach the host, which reach the
/// stored aggregate, and which write nothing at all.
/// </summary>
public sealed class ImportCoreRefusalTests
{
    private const string WhisparrRoot = "/whisparr-media";
    private const string ReportedPath = "/whisparr-media/scene.mp4";
    private const long ReportedSize = 10;

    [Fact]
    public async Task ANotFoundResolutionReachesNoHostImportAndOpensTheRootsLine()
    {
        var ingest = new Ingest();

        Assert.Equal(ImportOutcome.RefusedNotFound, await ingest.DeliverAsync());

        Assert.Empty(ingest.Library.Imported);
        var entry = Assert.Single((await ingest.StoredAsync()).ImportRefusals);
        Assert.Equal(WhisparrRoot, entry.Root);
        Assert.Equal(1, entry.CountSinceLastSuccess);
        Assert.Equal(
            ImportRefusalCause.NotFoundUnderAnyRoot,
            Assert.Single(entry.NewestPaths).Cause);
        Assert.Equal(ReportedPath, entry.NewestPaths[0].Path);
    }

    [Fact]
    public async Task AnAmbiguousResolutionReachesNoHostImportAndNamesItsOwnCause()
    {
        var ingest = new Ingest();
        ingest.Paths.Present["/data/scene.mp4"] = ReportedSize;
        ingest.Paths.Present["/data2/scene.mp4"] = ReportedSize;

        Assert.Equal(ImportOutcome.RefusedAmbiguous, await ingest.DeliverAsync());

        Assert.Empty(ingest.Library.Imported);
        var entry = Assert.Single((await ingest.StoredAsync()).ImportRefusals);
        Assert.Equal(
            ImportRefusalCause.AmbiguousCandidates,
            Assert.Single(entry.NewestPaths).Cause);
    }

    /// <summary>A root's own success clears its line and leaves another root's alone.</summary>
    [Fact]
    public async Task ASuccessfulImportClearsThatRootsLine()
    {
        var ingest = new Ingest();
        await ingest.DeliverAsync();
        await ingest.DeliverAsync(root: "/whisparr-other", path: "/whisparr-other/other.mp4");
        Assert.Equal(2, (await ingest.StoredAsync()).ImportRefusals.Count);

        ingest.Paths.Present["/data/scene.mp4"] = ReportedSize;
        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync());

        Assert.Equal(("/data/scene.mp4", (int?)null), Assert.Single(ingest.Library.Imported));
        Assert.Equal(
            "/whisparr-other",
            Assert.Single((await ingest.StoredAsync()).ImportRefusals).Root);
    }

    /// <summary>A delivery that told the aggregate nothing new does not write the blob.</summary>
    /// <remarks>
    /// A delivery arrives per file, so a save on every one would rewrite the whole blob per file. Its
    /// control is the first delivery, which does write.
    /// </remarks>
    [Fact]
    public async Task AnAggregateThatDidNotChangeIsNotWrittenBack()
    {
        var ingest = new Ingest();

        await ingest.DeliverAsync();
        var afterFirst = ingest.Store.SetCallCount;
        Assert.Equal(1, afterFirst);

        await ingest.DeliverAsync();

        Assert.Equal(afterFirst, ingest.Store.SetCallCount);
    }

    /// <summary>A success for a root that has no line writes nothing either.</summary>
    [Fact]
    public async Task ASuccessForARootWithNoLineWritesNothing()
    {
        var ingest = new Ingest();
        ingest.Paths.Present["/data/scene.mp4"] = ReportedSize;

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync());

        Assert.Equal(0, ingest.Store.SetCallCount);
    }

    /// <summary>A host import that could not be reached is its own cause.</summary>
    [Fact]
    public async Task AHostImportThatCouldNotBeReachedIsRecordedAsUnreadable()
    {
        var ingest = new Ingest(hostImportReached: false);
        ingest.Paths.Present["/data/scene.mp4"] = ReportedSize;

        Assert.Equal(ImportOutcome.RefusedHostImportUnavailable, await ingest.DeliverAsync());

        Assert.Equal(
            ImportRefusalCause.Unreadable,
            Assert.Single(Assert.Single((await ingest.StoredAsync()).ImportRefusals).NewestPaths).Cause);
    }

    /// <summary>A reported path under none of the instance's own roots is counted with no root.</summary>
    [Fact]
    public async Task APathUnderNoReportingRootIsCountedUnderTheStatedPlaceholder()
    {
        var ingest = new Ingest();

        Assert.Equal(
            ImportOutcome.RefusedPathOutsideEveryReportedRoot,
            await ingest.DeliverAsync(path: "/elsewhere/scene.mp4"));

        Assert.Empty(ingest.Library.Imported);
        Assert.Equal(
            ImportRefusalProjector.NoReportedRoot,
            Assert.Single((await ingest.StoredAsync()).ImportRefusals).Root);
    }

    /// <summary>
    /// A host with no library path is the host's own misconfiguration, so no Whisparr root is blamed
    /// for it.
    /// </summary>
    [Fact]
    public async Task AHostDeclaringNoLibraryPathBlamesNoWhisparrRoot()
    {
        var ingest = new Ingest(libraryRoots: []);

        Assert.Equal(ImportOutcome.RefusedNoLibraryRoots, await ingest.DeliverAsync());

        Assert.Empty((await ingest.StoredAsync()).ImportRefusals);
        Assert.Equal(0, ingest.Store.SetCallCount);
    }

    /// <summary>One ingest wired over fakes, with the store it wrote through readable afterwards.</summary>
    private sealed class Ingest(bool hostImportReached = true, IReadOnlyList<string>? libraryRoots = null)
    {
        public FakeStore Store { get; } = new();

        public RecordingLibrary Library { get; } =
            new(hostImportReached, libraryRoots ?? ["/data", "/data2"]);

        public StubPaths Paths { get; } = new();

        public Task<ImportOutcome> DeliverAsync(
            string root = WhisparrRoot, string path = ReportedPath)
            => new ImportCore(
                    new StubReportedRoots(root),
                    Library,
                    Paths,
                    new OptionsStore(Store),
                    NullLogger.Instance)
                .IngestAsync(
                    new ImportCandidate(WhisparrGeneration.V3, "Download", path, ReportedSize, null),
                    TestContext.Current.CancellationToken);

        public Task<WhisparrSyncOptions> StoredAsync()
            => new OptionsStore(Store).LoadAsync(TestContext.Current.CancellationToken);
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
