using Cove.Core.Entities;
using WhisparrSync.Library;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// Proves the single-scene read seam over an in-memory CoveContext: a seeded video round-trips
/// through <see cref="CoveLibraryPort.LoadVideoByIdAsync"/> with its StashDB-endpoint ids mapped exactly as
/// <c>LoadAllVideosAsync</c> maps them (same endpoint filter, same shape), and an absent id returns null.
/// </summary>
public sealed class SceneLibraryPortTests
{
    private const string StashEndpoint = "https://stashdb.org/graphql";
    private const string TpdbEndpoint = "https://theporndb.net/graphql";

    [Fact]
    public async Task LoadVideoById_ReturnsMappedScene_WithStashDbEndpointIds()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var folder = new Folder { Path = "/mnt/tank" };
            var video = new Video
            {
                Title = "Scene X",
                Date = new DateOnly(2021, 5, 6),
                RemoteIds =
                {
                    new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-a" },
                    new VideoRemoteId { Endpoint = "https://theporndb.net/graphql", RemoteId = "tpdb-1" },
                },
                Files =
                {
                    new VideoFile { Basename = "x.mkv", Path = "/mnt/tank/x.mkv", ParentFolder = folder },
                },
            };
            db.Set<Video>().Add(video);
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var loaded = await port.LoadVideoByIdAsync(video.Id);

            Assert.NotNull(loaded);
            Assert.Equal(video.Id, loaded!.CoveId);
            Assert.Equal("Scene X", loaded.Title);
            // Same StashDB-endpoint filter as LoadAllVideosAsync: the ThePornDB id is excluded.
            Assert.Equal(["uuid-a"], loaded.StashIds);
            Assert.Equal(["/mnt/tank/x.mkv"], loaded.FilePaths);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadVideoById_ReturnsNull_WhenIdAbsent()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            db.Set<Video>().Add(new Video { Title = "Only Scene" });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var loaded = await port.LoadVideoByIdAsync(999_999);

            Assert.Null(loaded);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
