using Cove.Core.Entities;
using WhisparrSync.Library;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// Proves the identity join at the DB seam: over an in-memory CoveContext, CoveLibraryPort projects a
/// video's StashDB ids, file paths, and fingerprints; filters remote ids to the configured endpoint
/// (excluding ThePornDB, case-insensitively); and leaves the ChangeTracker empty (AsNoTracking, no write).
/// </summary>
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
            var video = Assert.Single(await port.LoadAllVideosAsync());

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
            var video = Assert.Single(await port.LoadAllVideosAsync());

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
    public async Task IdentityHealth_countsUnidentifiedScenesAndWholeLibraryTotal()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Two StashDB-identified + one TPDB-only (no StashDB id) + one with no provider id at all. The total
            // must count all four (the whole-library figure the production System-principal read guarantees);
            // unidentified on v3 is the two without a StashDB id.
            db.Set<Video>().AddRange(
                new Video { Title = "Identified A", RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-a" } } },
                new Video { Title = "Identified B", RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-b" } } },
                new Video { Title = "TPDB only", RemoteIds = { new VideoRemoteId { Endpoint = TpdbEndpoint, RemoteId = "tpdb-1" } } },
                new Video { Title = "No id" });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var videos = await port.LoadAllVideosAsync();

            var (total, unidentified) = IdentityHealth.Count(videos, useTpdbEndpoint: false);

            Assert.Equal(4, total);
            Assert.Equal(2, unidentified);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task IdentityHealth_onV2CountsAgainstThePornDbId()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            db.Set<Video>().AddRange(
                new Video { Title = "StashDB only", RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-a" } } },
                new Video { Title = "TPDB identified", RemoteIds = { new VideoRemoteId { Endpoint = TpdbEndpoint, RemoteId = "tpdb-1" } } },
                new Video { Title = "No id" });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var videos = await port.LoadAllVideosAsync();

            // On v2 the connected-version id is ThePornDB: the StashDB-only + id-less videos are unidentified.
            var (total, unidentified) = IdentityHealth.Count(videos, useTpdbEndpoint: true);

            Assert.Equal(3, total);
            Assert.Equal(2, unidentified);
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
            await port.LoadAllVideosAsync();

            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
