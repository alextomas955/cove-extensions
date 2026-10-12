using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Import;

namespace WhisparrSync.Tests.TestSupport;

// A real relational library, so the host's own save is the one under test.
internal sealed class LibraryFixture : IAsyncDisposable
{
    private DbContext _db = null!;
    private SqliteConnection _connection = null!;
    private CoveConfiguration _config = null!;
    private IScanService? _scan;
    private ILogger _log = NullLogger.Instance;

    public CoveLibraryPort Port { get; private set; } = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    // A metadata double that has to reach back into this fixture cannot be constructed before it, so it
    // is supplied afterwards rather than the fixture being built in two halves.
    public void Reconfigure(IMetadataServerService? metadata)
        => Port = new CoveLibraryPort(_db, _scan, metadata, _config, _log);

    public void DropTheConnection() => _connection.Close();

    public async Task<string?> TitleOfAsync(int videoId)
        => (await _db.Set<Video>().AsNoTracking().FirstAsync(video => video.Id == videoId, Ct)).Title;

    // The file's stored path is left for the host's own save to compute from the folder, so the fixture
    // does not supply the value a read then checks.
    public async Task<int> SeedVideoWithFileAsync(
        string path, string? title = null, DateOnly? date = null)
    {
        var video = new Video { Title = title, Date = date };
        _db.Add(video);
        await _db.SaveChangesAsync(Ct);
        await AttachFileAsync(video.Id, path);
        return video.Id;
    }

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

    public async Task SeedIdentityAsync(int videoId, string endpoint, string remoteId)
    {
        _db.Add(new VideoRemoteId { VideoId = videoId, Endpoint = endpoint, RemoteId = remoteId });
        await _db.SaveChangesAsync(Ct);
    }

    public Task<List<VideoRemoteId>> IdentityRowsAsync()
        => _db.Set<VideoRemoteId>().AsNoTracking().ToListAsync(Ct);

    public Task<int> IdentityRowCountAsync()
        => _db.Set<VideoRemoteId>().AsNoTracking().CountAsync(Ct);

    // The item's own file-count figure, which the host recomputes on every save.
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
