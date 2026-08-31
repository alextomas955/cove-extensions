using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// A redelivery naming a different file for a scene the library already holds: the item it lands on,
/// what the two upgrade behaviours do with the file it supersedes, and what neither of them touches.
/// </summary>
/// <remarks>
/// The core-level assertions are on the ARGUMENT the host import seam received, not on how often it
/// was called: passing the existing item's key is the entire difference between a second item and the
/// same item now holding two files, and a call count cannot see it.
/// </remarks>
public sealed class UpgradePathTests
{
    private const string WhisparrRoot = "/whisparr-media";
    private const string RemoteId = "e1a5c0d2-0000-4000-8000-000000000005";
    private const long ReportedSize = 10;

    [Fact]
    public async Task ARedeliveryNamingADifferentFileIsImportedOntoTheItemTheIdentifierNames()
    {
        var ingest = new Ingest();
        ingest.Library.ExistingIdentities.Add((7, "https://stashdb.org/graphql", RemoteId));

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync("upgrade.mp4"));

        Assert.Equal(("/data/upgrade.mp4", (int?)7), Assert.Single(ingest.Library.Imported));
    }

    /// <summary>
    /// The discriminating control: an identifier naming nothing is imported with no key at all, so
    /// the key above is there because the identifier resolved and not because one is always passed.
    /// </summary>
    [Fact]
    public async Task ARedeliveryWhoseIdentifierNamesNoItemIsImportedAsANewOne()
    {
        var ingest = new Ingest();

        Assert.Equal(ImportOutcome.Imported, await ingest.DeliverAsync("first.mp4"));

        Assert.Equal(("/data/first.mp4", (int?)null), Assert.Single(ingest.Library.Imported));
    }

    /// <summary>
    /// An identifier two items carry reaches no host import at all. The identifier is authenticated
    /// only by the shared secret the callback checks, so attaching a file to the wrong item on a
    /// coincidental match would be a write into the library driven by an unsigned value.
    /// </summary>
    [Fact]
    public async Task AnIdentifierTwoItemsCarryReachesNoHostImportAndDetachesNothing()
    {
        var ingest = new Ingest();
        ingest.Library.IdentityIsAmbiguous = true;
        await ingest.StoreAsync(UpgradeBehavior.Replace);

        Assert.Equal(ImportOutcome.RefusedAmbiguousIdentity, await ingest.DeliverAsync("upgrade.mp4"));

        Assert.Empty(ingest.Library.Imported);
        Assert.Empty(ingest.Library.Detached);
    }

    [Fact]
    public async Task ARedeliveryNamingTheSamePathReachesNoHostImportAndDetachesNothing()
    {
        var ingest = new Ingest();
        ingest.Library.ExistingIdentities.Add((7, "https://stashdb.org/graphql", RemoteId));
        ingest.Library.Held["/data/held.mp4"] = 7;
        await ingest.StoreAsync(UpgradeBehavior.Replace);

        Assert.Equal(ImportOutcome.AlreadyHeld, await ingest.DeliverAsync("held.mp4"));

        Assert.Empty(ingest.Library.Imported);
        Assert.Empty(ingest.Library.Detached);
    }

    /// <summary>The default: the new file is attached and nothing else is touched.</summary>
    [Fact]
    public async Task UnderTheDefaultBehaviourTheSupersededRowIsLeftAttached()
    {
        var ingest = new Ingest();
        ingest.Library.ExistingIdentities.Add((7, "https://stashdb.org/graphql", RemoteId));

        await ingest.DeliverAsync("upgrade.mp4");

        Assert.Empty(ingest.Library.Detached);
    }

    [Fact]
    public async Task UnderTheOtherBehaviourTheSupersededRowIsDetachedFromTheItem()
    {
        var ingest = new Ingest();
        ingest.Library.ExistingIdentities.Add((7, "https://stashdb.org/graphql", RemoteId));
        await ingest.StoreAsync(UpgradeBehavior.Replace);

        await ingest.DeliverAsync("upgrade.mp4");

        Assert.Equal((7, "/data/upgrade.mp4"), Assert.Single(ingest.Library.Detached));
    }

    /// <summary>A first import is not an upgrade, so the other behaviour detaches nothing there.</summary>
    [Fact]
    public async Task AFirstImportDetachesNothingUnderEitherBehaviour()
    {
        var ingest = new Ingest();
        await ingest.StoreAsync(UpgradeBehavior.Replace);

        await ingest.DeliverAsync("first.mp4");

        Assert.Empty(ingest.Library.Detached);
    }

    /// <summary>
    /// A detach clears the row's video key and leaves the row, and the item's own figures are right
    /// afterwards because the host's save recomputes them.
    /// </summary>
    /// <remarks>
    /// Over a real relational context, so the recomputation under test is the host's own rather than
    /// a value this test supplied.
    /// </remarks>
    [Fact]
    public async Task ADetachLeavesOneFileOnTheItemAndTheDetachedRowStillThereWithNoVideoKey()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var videoId = await library.SeedVideoWithFileAsync("/data/old.mp4");
        await library.AttachFileAsync(videoId, "/data/new.mp4");

        Assert.Equal(2, await library.FileCountOfAsync(videoId));

        Assert.Equal(1, await library.Port.DetachSupersededFilesAsync(videoId, "/data/new.mp4", Ct));

        Assert.Equal(1, await library.FileCountOfAsync(videoId));
        Assert.Equal([("/data/new.mp4", (int?)videoId), ("/data/old.mp4", null)], await library.FilesAsync());
    }

    /// <summary>A detach that finds only the kept row writes nothing.</summary>
    [Fact]
    public async Task ADetachOverAnItemHoldingOnlyTheKeptFileChangesNothing()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var videoId = await library.SeedVideoWithFileAsync("/data/new.mp4");

        Assert.Equal(0, await library.Port.DetachSupersededFilesAsync(videoId, "/data/new.mp4", Ct));

        Assert.Equal([("/data/new.mp4", (int?)videoId)], await library.FilesAsync());
    }

    /// <summary>The setting's reader ships with its control: a save applies it and a read returns it.</summary>
    [Fact]
    public void ASavedUpgradeBehaviourIsAppliedAndReadBack()
    {
        var stored = SettingsProjector.Apply(
            new WhisparrSyncOptions(),
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V3, null, null, UpgradeBehavior.Replace));

        Assert.Equal(UpgradeBehavior.Replace, stored.UpgradeBehavior);
        Assert.Equal(
            UpgradeBehavior.Replace,
            SettingsProjector.ToView(stored, v3KeyIsSet: false, v2KeyIsSet: false).UpgradeBehavior);
    }

    /// <summary>A save that omits it leaves the stored value, which the connection form relies on.</summary>
    [Fact]
    public void ASaveThatOmitsTheUpgradeBehaviourLeavesTheStoredOne()
        => Assert.Equal(
            UpgradeBehavior.Replace,
            SettingsProjector.Apply(
                new WhisparrSyncOptions { UpgradeBehavior = UpgradeBehavior.Replace },
                new WhisparrSyncSettingsSaveRequest(WhisparrGeneration.V3, null, null))
                .UpgradeBehavior);

    /// <summary>
    /// Neither behaviour creates a capability to move, rename or delete a file, and this is a
    /// property of the seam rather than of the code that calls it.
    /// </summary>
    [Fact]
    public void TheLibrarySeamDeclaresNoMemberThatCouldMoveRenameOrDeleteAFile()
    {
        var named = typeof(ICoveLibraryPort)
            .GetMembers()
            .Select(member => member.Name)
            .Where(name => name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Move", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Rename", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(named);

        // The control: the seam does have members, so the emptiness above is about the vocabulary
        // rather than about a type with nothing on it.
        Assert.NotEmpty(typeof(ICoveLibraryPort).GetMembers());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One ingest over fakes, with the import and detach seams recorded.</summary>
    private sealed class Ingest
    {
        public FakeStore Store { get; } = new();

        public RecordingLibrary Library { get; } = new(reached: true, ["/data"]);

        public StubPaths Paths { get; } = new();

        /// <summary>Stores the upgrade behaviour the delivery below is read under.</summary>
        public Task StoreAsync(UpgradeBehavior behaviour)
            => new OptionsStore(Store).SaveAsync(
                new WhisparrSyncOptions { UpgradeBehavior = behaviour }, Ct);

        public Task<ImportOutcome> DeliverAsync(string basename)
        {
            Paths.Present["/data/" + basename] = ReportedSize;
            return new ImportCore(
                    new StubReportedRoots(WhisparrRoot),
                    Library,
                    Paths,
                    new OptionsStore(Store),
                    new FollowUpScanCoalescer(TimeProvider.System, NullLogger.Instance),
                    NullLogger.Instance)
                .IngestAsync(
                    new ImportCandidate(
                        WhisparrGeneration.V3,
                        "Download",
                        WhisparrRoot + "/" + basename,
                        ReportedSize,
                        RemoteId),
                    Ct);
        }
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

    /// <summary>A real relational library, so the host's own save is the one under test.</summary>
    private sealed class LibraryFixture : IAsyncDisposable
    {
        private CoveContext _db = null!;
        private SqliteConnection _connection = null!;

        public CoveLibraryPort Port { get; private set; } = null!;

        public static async Task<LibraryFixture> CreateAsync()
        {
            var fixture = new LibraryFixture();
            (fixture._db, fixture._connection) = await CoveContextFactory.CreateSqliteContextAsync();
            fixture.Port = new CoveLibraryPort(
                fixture._db, scan: null, metadata: null, new CoveConfiguration());
            return fixture;
        }

        public async Task<int> SeedVideoWithFileAsync(string path)
        {
            var video = new Video();
            _db.Add(video);
            await _db.SaveChangesAsync(Ct);
            await AttachFileAsync(video.Id, path);
            return video.Id;
        }

        /// <summary>
        /// Attaches one more file to an item. The stored path is left for the host's own save to
        /// compute from the folder, so the fixture does not supply the value a read then checks.
        /// </summary>
        public async Task AttachFileAsync(int videoId, string path)
        {
            _db.Add(new VideoFile
            {
                Basename = path[(path.LastIndexOf('/') + 1)..],
                ParentFolder = await FolderAsync(path[..path.LastIndexOf('/')]),
                VideoId = videoId,
            });
            await _db.SaveChangesAsync(Ct);
        }

        /// <summary>The item's OWN file-count figure, which the host recomputes on every save.</summary>
        public async Task<int> FileCountOfAsync(int videoId)
            => (await _db.Set<Video>().AsNoTracking().FirstAsync(video => video.Id == videoId, Ct))
                .FileCount;

        public async Task<List<(string Path, int? VideoId)>> FilesAsync()
            => [.. (await _db.Set<VideoFile>().AsNoTracking().ToListAsync(Ct))
                .Select(file => (file.Path, file.VideoId))
                .OrderBy(row => row.Path, StringComparer.Ordinal)];

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private async Task<Folder> FolderAsync(string path)
            => await _db.Set<Folder>().FirstOrDefaultAsync(folder => folder.Path == path, Ct)
                ?? await AddFolderAsync(path);

        private async Task<Folder> AddFolderAsync(string path)
        {
            var folder = new Folder { Path = path };
            _db.Add(folder);
            await _db.SaveChangesAsync(Ct);
            return folder;
        }
    }
}
