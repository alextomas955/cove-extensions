using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

        Assert.Equal(videoId, await library.Port.VideoHoldingFileAtAsync("/data/scene.mp4", Ct));
        Assert.Null(await library.Port.VideoHoldingFileAtAsync("/data/unknown.mp4", Ct));
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

    [Fact]
    public async Task AnAbsentScanServiceReportsTheHostImportAsUnreachedAndRegistersNothing()
    {
        await using var library = await LibraryFixture.CreateAsync();

        var imported = await library.Port.ImportVideoAsync("/data/scene.mp4", null, Ct);

        Assert.False(imported.Reached);
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One real relational library, with the port wired over its context.</summary>
    private sealed class LibraryFixture : IAsyncDisposable
    {
        private CoveContext _db = null!;
        private SqliteConnection _connection = null!;

        public CoveLibraryPort Port { get; private set; } = null!;

        public static async Task<LibraryFixture> CreateAsync(
            IReadOnlyList<string>? configuredEndpoints = null)
        {
            var fixture = new LibraryFixture();
            (fixture._db, fixture._connection) = await CoveContextFactory.CreateSqliteContextAsync();

            var config = new CoveConfiguration();
            foreach (var endpoint in configuredEndpoints ?? [])
            {
                config.Scraping.MetadataServers.Add(new MetadataServerInstance { Endpoint = endpoint });
            }

            fixture.Port = new CoveLibraryPort(fixture._db, scan: null, metadata: null, config);
            return fixture;
        }

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
