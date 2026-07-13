using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Library;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// Proves the MATCH-01 join at the DB seam: over an in-memory CoveContext, CoveLibraryPort projects a
/// video's StashDB ids, file paths, and fingerprints; filters remote ids to the configured endpoint
/// (excluding ThePornDB, case-insensitively); and leaves the ChangeTracker empty (AsNoTracking, no write).
/// </summary>
public sealed class CoveLibraryPortTests
{
    private const string StashEndpoint = "https://stashdb.org/graphql";

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

            var port = new CoveLibraryPort(db, StashEndpoint);
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

            var port = new CoveLibraryPort(db, StashEndpoint);
            var video = Assert.Single(await port.LoadAllVideosAsync());

            Assert.Equal(["uuid-a"], video.StashIds);
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

            var port = new CoveLibraryPort(db, StashEndpoint);
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
