using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Import;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// The port's reads, its one write, and the read that stands in for the missing unique constraint.
/// </summary>
/// <remarks>
/// Over a real relational context rather than the non-relational provider, so the host's own indexes,
/// transactions and per-principal query filters are the real ones.
/// </remarks>
public sealed class CoveLibraryPortTests
{
    private const string ConfiguredEndpoint = "https://stashdb.org/graphql";
    private const string OtherSpelling = "https://api.stashdb.org/graphql";
    private const string RemoteId = "e1a5c0d2-0000-4000-8000-000000000001";

    [Fact]
    public async Task AFileTheLibraryHoldsAnswersItsVideoAndAnUnknownPathAnswersNothing()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var videoId = await library.SeedVideoWithFileAsync("/data/scene.mp4");

        Assert.Equal(videoId, (await library.Port.HeldFileAtAsync("/data/scene.mp4", Ct))?.VideoId);
        Assert.Null(await library.Port.HeldFileAtAsync("/data/unknown.mp4", Ct));
    }

    /// <summary>
    /// A row this extension itself detached still reads as a row, over a real relational context.
    /// </summary>
    /// <remarks>
    /// The detached state is produced by the product's own write rather than seeded, so what is read
    /// back is the database state a user reaches under the Replace behaviour. Answering null here
    /// would send the redelivery of that path to the host's import with no item to attach it to,
    /// which is the one input that import answers by throwing.
    /// </remarks>
    [Fact]
    public async Task AFileRowThisExtensionDetachedIsStillReadAsHeldByTheLibrary()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var videoId = await library.SeedVideoWithFileAsync("/data/first.mp4");
        await library.AttachFileAsync(videoId, "/data/second.mp4");

        Assert.Equal(1, await library.Port.DetachSupersededFilesAsync(videoId, "/data/second.mp4", Ct));

        var detached = await library.Port.HeldFileAtAsync("/data/first.mp4", Ct);
        Assert.NotNull(detached);
        Assert.Null(detached.VideoId);

        Assert.Equal(videoId, (await library.Port.HeldFileAtAsync("/data/second.mp4", Ct))?.VideoId);
        Assert.Null(await library.Port.HeldFileAtAsync("/data/unknown.mp4", Ct));
    }

    /// <summary>
    /// Each exception the host's own import raises is answered rather than propagated, and reported
    /// exactly once.
    /// </summary>
    /// <remarks>
    /// The scan service is a double, because this extension does not reference the assembly the real
    /// one lives in. That the real host raises these is proven where the real host runs, in the
    /// containerized end-to-end spec.
    /// </remarks>
    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task AHostImportThatRaisesIsContainedRatherThanPropagated(Type raised)
    {
        var log = new RecordingLogger();
        await using var library = await LibraryFixture.CreateAsync(
            scan: new RaisingScanService((Exception)Activator.CreateInstance(raised)!), log: log);

        var imported = await library.Port.ImportVideoAsync("/data/scene.mp4", null, Ct);

        Assert.Equal(LibraryImportOutcome.HostRefused, imported.Outcome);
        Assert.Null(imported.VideoId);
        Assert.Single(log.ContainedHostImportLines);
    }

    /// <summary>
    /// The one line a contained host import emits carries no part of the path the failure names.
    /// </summary>
    /// <remarks>
    /// <see cref="FileNotFoundException"/> is one of the two the host's import raises, and its message
    /// quotes the file it could not find. The message here is composed by the runtime from the file
    /// name, so the value the assertion searches for is not one this test wrote into it.
    /// <para>
    /// The line is read as a sink writes it - the rendered message together with the exception the
    /// logger was handed - because a sink writes both.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AContainedHostImportIsLoggedWithoutThePathTheFailureNames()
    {
        const string path = "/data/whisparr-verified/scene.mp4";
        var log = new RecordingLogger();
        await using var library = await LibraryFixture.CreateAsync(
            scan: new RaisingScanService(new FileNotFoundException(message: null, fileName: path)),
            log: log);

        await library.Port.ImportVideoAsync(path, null, Ct);

        var line = Assert.Single(log.ContainedHostImportLines);
        Assert.DoesNotContain(path, line, StringComparison.Ordinal);
    }

    /// <summary>An exception the host raises that this product does not know about still propagates.</summary>
    /// <remarks>
    /// The discriminating control for the containment above: without it, a catch of every exception
    /// would satisfy the same assertions while hiding a defect.
    /// </remarks>
    [Fact]
    public async Task AnExceptionTheHostImportIsNotKnownToRaiseIsNotContained()
    {
        await using var library = await LibraryFixture.CreateAsync(
            scan: new RaisingScanService(new NotSupportedException()));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => library.Port.ImportVideoAsync("/data/scene.mp4", null, Ct));
    }

    /// <summary>
    /// The stored spelling and the queried one differ, which is the whole reason the read goes
    /// through the endpoint rule rather than through string equality.
    /// </summary>
    [Fact]
    public async Task AnIdentifierStoredUnderAnotherSpellingOfTheSourceStillResolves()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var videoId = await library.SeedVideoWithFileAsync("/data/scene.mp4");
        await library.SeedIdentityAsync(videoId, OtherSpelling, RemoteId);

        var resolution = await library.Port.ResolveByRemoteIdAsync(ConfiguredEndpoint, RemoteId, Ct);

        Assert.Equal(videoId, resolution.VideoId);
        Assert.False(resolution.Ambiguous);
    }

    [Fact]
    public async Task AnIdentifierNoRowCarriesResolvesToNothingWithoutThrowing()
    {
        await using var library = await LibraryFixture.CreateAsync();
        await library.SeedVideoWithFileAsync("/data/scene.mp4");

        var resolution = await library.Port.ResolveByRemoteIdAsync(ConfiguredEndpoint, RemoteId, Ct);

        Assert.Null(resolution.VideoId);
        Assert.False(resolution.Ambiguous);
    }

    /// <summary>A library holding no identity rows at all answers unmatched and writes nothing.</summary>
    [Fact]
    public async Task ALibraryWithNoIdentityRowsAnswersUnmatchedForEveryVideoAndWritesNothing()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var first = await library.SeedVideoWithFileAsync("/data/first.mp4");
        var second = await library.SeedVideoWithFileAsync("/data/second.mp4");

        Assert.Null((await library.Port.ResolveByRemoteIdAsync(ConfiguredEndpoint, RemoteId, Ct)).VideoId);
        Assert.False(await library.Port.CarriesIdentityAsync(first, ConfiguredEndpoint, Ct));
        Assert.False(await library.Port.CarriesIdentityAsync(second, ConfiguredEndpoint, Ct));

        Assert.Equal(0, await library.IdentityRowCountAsync());
    }

    /// <summary>
    /// Two reads of one match, each derived live, with nothing written between them. What makes this
    /// checkable is the row count either side: a read that cached its answer would have to store it.
    /// </summary>
    [Fact]
    public async Task TwoReadsOfOneMatchAgreeAndNeitherWritesAnything()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var videoId = await library.SeedVideoWithFileAsync("/data/scene.mp4");
        await library.SeedIdentityAsync(videoId, ConfiguredEndpoint, RemoteId);
        var seeded = await library.IdentityRowCountAsync();

        var first = await library.Port.ResolveByRemoteIdAsync(ConfiguredEndpoint, RemoteId, Ct);
        var second = await library.Port.ResolveByRemoteIdAsync(OtherSpelling, RemoteId, Ct);

        Assert.Equal(videoId, first.VideoId);
        Assert.Equal(first.VideoId, second.VideoId);
        Assert.Equal(seeded, await library.IdentityRowCountAsync());
    }

    [Fact]
    public async Task AnIdentifierCarriedByTwoVideosIsRefusedRatherThanAnswered()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var first = await library.SeedVideoWithFileAsync("/data/first.mp4");
        var second = await library.SeedVideoWithFileAsync("/data/second.mp4");
        await library.SeedIdentityAsync(first, ConfiguredEndpoint, RemoteId);
        await library.SeedIdentityAsync(second, OtherSpelling, RemoteId);

        var resolution = await library.Port.ResolveByRemoteIdAsync(ConfiguredEndpoint, RemoteId, Ct);

        Assert.True(resolution.Ambiguous);
        Assert.Null(resolution.VideoId);
    }

    /// <summary>
    /// Two videos sharing a title, a date and a file name, carrying different identifiers. Nothing on
    /// the match path may notice any of the three.
    /// </summary>
    [Fact]
    public async Task TwoVideosWithIdenticalTitlesAndDifferentIdentifiersDoNotMatchEachOther()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var first = await library.SeedVideoWithFileAsync(
            "/data/one/scene.mp4", title: "The Same Title", date: new DateOnly(2020, 1, 1));
        var second = await library.SeedVideoWithFileAsync(
            "/data/two/scene.mp4", title: "The Same Title", date: new DateOnly(2020, 1, 1));
        await library.SeedIdentityAsync(first, ConfiguredEndpoint, RemoteId);
        await library.SeedIdentityAsync(second, ConfiguredEndpoint, "a-different-identifier");

        Assert.Equal(
            first, (await library.Port.ResolveByRemoteIdAsync(ConfiguredEndpoint, RemoteId, Ct)).VideoId);
        Assert.Equal(
            second,
            (await library.Port.ResolveByRemoteIdAsync(ConfiguredEndpoint, "a-different-identifier", Ct)).VideoId);
    }

    [Fact]
    public async Task StampingAVideoThatCarriesNoRowForTheSourceInsertsExactlyOne()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var videoId = await library.SeedVideoWithFileAsync("/data/scene.mp4");

        Assert.True(await library.Port.StampIdentityAsync(videoId, ConfiguredEndpoint, RemoteId, Ct));

        var row = Assert.Single(await library.IdentityRowsAsync());
        Assert.Equal(videoId, row.VideoId);
        Assert.Equal(ConfiguredEndpoint, row.Endpoint);
        Assert.Equal(RemoteId, row.RemoteId);
    }

    /// <summary>
    /// The database has no unique constraint on the video-and-endpoint pair, so this read is the only
    /// thing between one source and two rows.
    /// </summary>
    [Fact]
    public async Task StampingUnderTwoSpellingsOfOneSourceLeavesExactlyOneRowUntouched()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var videoId = await library.SeedVideoWithFileAsync("/data/scene.mp4");

        Assert.True(await library.Port.StampIdentityAsync(videoId, ConfiguredEndpoint, RemoteId, Ct));
        Assert.False(await library.Port.StampIdentityAsync(videoId, OtherSpelling, "a-later-identifier", Ct));

        var row = Assert.Single(await library.IdentityRowsAsync());
        Assert.Equal(ConfiguredEndpoint, row.Endpoint);
        Assert.Equal(RemoteId, row.RemoteId);
    }

    [Fact]
    public async Task AVideoCarryingARowUnderAnotherSpellingReadsAsCarryingTheSource()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var videoId = await library.SeedVideoWithFileAsync("/data/scene.mp4");
        await library.SeedIdentityAsync(videoId, OtherSpelling, RemoteId);

        Assert.True(await library.Port.CarriesIdentityAsync(videoId, ConfiguredEndpoint, Ct));
        Assert.False(await library.Port.CarriesIdentityAsync(videoId, "https://theporndb.net/graphql", Ct));
    }

    [Fact]
    public async Task TheConfiguredMetadataEndpointsAreReadFromTheHostWithBlanksDropped()
    {
        await using var library = await LibraryFixture.CreateAsync(
            configuredEndpoints: [ConfiguredEndpoint, "   "]);

        Assert.Equal([ConfiguredEndpoint], library.Port.ConfiguredMetadataEndpoints);
    }

    /// <summary>
    /// An import service the container could not produce is its own outcome, not the one a declined
    /// file gets.
    /// </summary>
    [Fact]
    public async Task AnAbsentScanServiceReportsTheHostImportAsUnavailableAndRegistersNothing()
    {
        await using var library = await LibraryFixture.CreateAsync();

        var imported = await library.Port.ImportVideoAsync("/data/scene.mp4", null, Ct);

        Assert.Equal(LibraryImportOutcome.ServiceUnavailable, imported.Outcome);
        Assert.Null(imported.VideoId);
    }

    /// <summary>An absent metadata service is an enrichment that did not happen, never a throw.</summary>
    [Fact]
    public async Task AnAbsentMetadataServiceEnrichesNothingAndDoesNotThrow()
    {
        await using var library = await LibraryFixture.CreateAsync();
        var videoId = await library.SeedVideoWithFileAsync("/data/scene.mp4");

        Assert.False(await library.Port.EnrichAsync(videoId, ConfiguredEndpoint, RemoteId, Ct));
    }

    /// <summary>A source that applied a record and a library that took it is the answered case.</summary>
    /// <remarks>
    /// The positive control for the case below: without it, a refusal to commit could equally mean the
    /// merge is never reached at all.
    /// </remarks>
    [Fact]
    public async Task AMergedRecordThatWasSavedIsAnsweredAsApplied()
    {
        await using var library = await LibraryFixture.CreateAsync(
            configuredEndpoints: [ConfiguredEndpoint],
            metadata: new MergingMetadataServer(video => video.Title = "the source's title"));
        var videoId = await library.SeedVideoWithFileAsync("/data/scene.mp4");

        Assert.True(await library.Port.EnrichAsync(videoId, ConfiguredEndpoint, RemoteId, Ct));
        Assert.Equal("the source's title", await library.TitleOfAsync(videoId));
    }

    /// <summary>
    /// A save that failed after the merge answered is raised as its own failure, not as the source's.
    /// </summary>
    /// <remarks>
    /// Both halves of the call otherwise reach the caller as one broad catch, which can then only name
    /// the source — and here the source applied its record. The record is applied and the connection
    /// dropped inside the merge, so there is a real change to commit and no library to commit it to.
    /// </remarks>
    [Fact]
    public async Task ASaveThatFailedAfterTheMergeIsRaisedAsAnUncommittedEnrichment()
    {
        await using var library = await LibraryFixture.CreateAsync(
            configuredEndpoints: [ConfiguredEndpoint]);
        var videoId = await library.SeedVideoWithFileAsync("/data/scene.mp4");
        library.Reconfigure(new MergingMetadataServer(video =>
        {
            video.Title = "the source's title";
            library.DropTheConnection();
        }));

        var raised = await Assert.ThrowsAsync<EnrichmentNotCommittedException>(
            () => library.Port.EnrichAsync(videoId, ConfiguredEndpoint, RemoteId, Ct));

        Assert.NotNull(raised.InnerException);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A host scan service whose video import raises, standing in for the real one.</summary>
    private sealed class RaisingScanService(Exception raised) : IScanService
    {
        public string StartScan(ScanOperationOptions? options = null) => throw new NotSupportedException();

        public Task<int> ImportDownloadedVideoAsync(string path, int? videoId, CancellationToken ct = default)
            => Task.FromException<int>(raised);

        public Task<int> ImportDownloadedImageAsync(string path, int? imageId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> ImportDownloadedGalleryAsync(string path, int? galleryId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> ImportDownloadedAudioAsync(string path, int? audioId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> ImportDownloadedTextAsync(string path, int? textDocumentId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A metadata source that applies a record and leaves the save to the caller, as the host's own
    /// does.
    /// </summary>
    /// <remarks>
    /// The host's merge mutates the entity it is handed and saves nothing, so what a test does inside
    /// <paramref name="apply"/> lands in exactly the window the port then has to commit.
    /// </remarks>
    private sealed class MergingMetadataServer(Action<Video> apply) : IMetadataServerService
    {
        public Task<bool> MergeVideoAsync(
            Video video,
            string endpoint,
            string videoId,
            MetadataServerVideoImportRequestDto? importConfig,
            CancellationToken ct)
        {
            apply(video);
            return Task.FromResult(true);
        }
    }

    /// <summary>Keeps the contained-host-import lines, by event id, as a sink would write them.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private const int ContainedHostImportEventId = 2111;

        private readonly List<string> _lines = [];

        public IReadOnlyList<string> ContainedHostImportLines => _lines;

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
            if (eventId.Id == ContainedHostImportEventId)
            {
                _lines.Add($"{formatter(state, exception)} {exception}");
            }
        }
    }

    /// <summary>One real relational library, with the port wired over its context.</summary>
    private sealed class LibraryFixture : IAsyncDisposable
    {
        private CoveContext _db = null!;
        private SqliteConnection _connection = null!;
        private CoveConfiguration _config = null!;
        private IScanService? _scan;
        private ILogger _log = NullLogger.Instance;

        public CoveLibraryPort Port { get; private set; } = null!;

        public static async Task<LibraryFixture> CreateAsync(
            IReadOnlyList<string>? configuredEndpoints = null,
            IScanService? scan = null,
            ILogger? log = null,
            IMetadataServerService? metadata = null)
        {
            var fixture = new LibraryFixture();
            (fixture._db, fixture._connection) = await CoveContextFactory.CreateSqliteContextAsync();

            var config = new CoveConfiguration();
            foreach (var endpoint in configuredEndpoints ?? [])
            {
                config.Scraping.MetadataServers.Add(new MetadataServerInstance { Endpoint = endpoint });
            }

            fixture._config = config;
            fixture._scan = scan;
            fixture._log = log ?? NullLogger.Instance;
            fixture.Reconfigure(metadata);
            return fixture;
        }

        /// <summary>Rebuilds the port over the same library with <paramref name="metadata"/>.</summary>
        /// <remarks>
        /// A metadata double that has to reach back into this fixture cannot be constructed before it,
        /// so it is supplied afterwards rather than the fixture being built in two halves.
        /// </remarks>
        public void Reconfigure(IMetadataServerService? metadata)
            => Port = new CoveLibraryPort(_db, _scan, metadata, _config, _log);

        /// <summary>Drops the library's connection, so the next save cannot be committed.</summary>
        public void DropTheConnection() => _connection.Close();

        public async Task<string?> TitleOfAsync(int videoId)
            => (await _db.Set<Video>().AsNoTracking().FirstAsync(video => video.Id == videoId, Ct)).Title;

        /// <summary>
        /// Seeds one video with one file. The file's stored path is left for the host's own save to
        /// compute from the folder, so the fixture does not supply the value a read then checks.
        /// </summary>
        public async Task<int> SeedVideoWithFileAsync(
            string path, string? title = null, DateOnly? date = null)
        {
            var folder = await FolderAsync(Directory(path));
            var video = new Video { Title = title, Date = date };
            _db.Add(video);
            await _db.SaveChangesAsync(Ct);

            _db.Add(new VideoFile
            {
                Basename = path[(path.LastIndexOf('/') + 1)..],
                ParentFolder = folder,
                VideoId = video.Id,
            });
            await _db.SaveChangesAsync(Ct);
            return video.Id;
        }

        /// <summary>Gives <paramref name="videoId"/> a second file, so a detach has one to supersede.</summary>
        public async Task AttachFileAsync(int videoId, string path)
        {
            _db.Add(new VideoFile
            {
                Basename = path[(path.LastIndexOf('/') + 1)..],
                ParentFolder = await FolderAsync(Directory(path)),
                VideoId = videoId,
            });
            await _db.SaveChangesAsync(Ct);
        }

        public async Task SeedIdentityAsync(int videoId, string endpoint, string remoteId)
        {
            _db.Add(new VideoRemoteId { VideoId = videoId, Endpoint = endpoint, RemoteId = remoteId });
            await _db.SaveChangesAsync(Ct);
        }

        public Task<List<VideoRemoteId>> IdentityRowsAsync()
            => _db.Set<VideoRemoteId>().AsNoTracking().ToListAsync(Ct);

        public Task<int> IdentityRowCountAsync()
            => _db.Set<VideoRemoteId>().AsNoTracking().CountAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static string Directory(string path) => path[..path.LastIndexOf('/')];

        /// <summary>The folder row for <paramref name="path"/>, created once. Its path is unique.</summary>
        private async Task<Folder> FolderAsync(string path)
        {
            var existing = await _db.Set<Folder>().FirstOrDefaultAsync(folder => folder.Path == path, Ct);
            if (existing is not null)
            {
                return existing;
            }

            var folder = new Folder { Path = path };
            _db.Add(folder);
            await _db.SaveChangesAsync(Ct);
            return folder;
        }
    }
}
