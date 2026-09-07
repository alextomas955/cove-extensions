using Cove.Core.Entities;
using WhisparrSync.Library;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// Proves the identity join at the DB seam: over an in-memory CoveContext, CoveLibraryPort projects a
/// video's StashDB ids, file paths, and fingerprints; filters remote ids to the configured endpoint
/// (excluding ThePornDB, case-insensitively); and leaves the ChangeTracker empty (AsNoTracking, no write).
/// </summary>
[Trait("Tier", "L1")]
public sealed class CoveLibraryPortTests
{
    private const string StashEndpoint = "https://stashdb.org/graphql";
    private const string TpdbEndpoint = "https://theporndb.net/graphql";

    [Fact]
    public async Task CoveLibraryPort_ProjectsStashIdPathAndFingerprints()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var folder = new Folder { Path = "/mnt/tank" };
            db.Set<Video>().Add(new Video
            {
                Title = "Scene X",
                Date = new DateOnly(2020, 1, 2),
                RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-a" } },
                Files =
                {
                    new VideoFile
                    {
                        Basename = "x.mkv",
                        Path = "/mnt/tank/x.mkv",
                        ParentFolder = folder,
                        Fingerprints = { new FileFingerprint { Type = "oshash", Value = "abc" } },
                    },
                },
            });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var video = Assert.Single(await Materialize(port));

            Assert.Equal("Scene X", video.Title);
            Assert.Equal(new DateOnly(2020, 1, 2), video.Date);
            Assert.Equal(["uuid-a"], video.StashIds);
            Assert.Equal(["/mnt/tank/x.mkv"], video.FilePaths);
            Assert.Contains(video.Fingerprints, f => f.Type == "oshash" && f.Value == "abc");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task CoveLibraryPort_FiltersByConfiguredEndpoint()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            db.Set<Video>().Add(new Video
            {
                Title = "Scene Y",
                RemoteIds =
                {
                    // Stored upper/mixed-case still matches the configured endpoint (case-insensitive).
                    new VideoRemoteId { Endpoint = "HTTPS://StashDB.org/GraphQL", RemoteId = "uuid-a" },
                    new VideoRemoteId { Endpoint = "https://theporndb.net/graphql", RemoteId = "tpdb-1" },
                },
            });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var video = Assert.Single(await Materialize(port));

            Assert.Equal(["uuid-a"], video.StashIds);
            Assert.Equal(["tpdb-1"], video.TpdbIds);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task CoveLibraryPort_IsReadOnly()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            db.Set<Video>().Add(new Video { Title = "Scene Z" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            await Materialize(port);

            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // The whole-library read is a stream; these assertions are about what one row PROJECTS to, so they fold it
    // to a list rather than restating the mapping.
    private static async Task<List<CoveVideo>> Materialize(CoveLibraryPort port)
    {
        var videos = new List<CoveVideo>();
        await foreach (var video in port.StreamAllVideosAsync())
        {
            videos.Add(video);
        }

        return videos;
    }
}
