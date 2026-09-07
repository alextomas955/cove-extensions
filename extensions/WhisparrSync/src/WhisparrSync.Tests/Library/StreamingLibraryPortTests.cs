using Cove.Core.Entities;
using WhisparrSync.Contracts;
using WhisparrSync.Library;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// Proves the bounded-read seam over an in-memory CoveContext: the video and file-path streams cross the keyset
/// page boundary without dropping or duplicating a row and project identically to the materialized read, and the
/// owned-remote-id lookup answers only about the ids it was asked about, case-insensitively.
/// </summary>
[Trait("Tier", "L1")]
public sealed class StreamingLibraryPortTests
{
    private const string StashEndpoint = "https://stashdb.org/graphql";
    private const string TpdbEndpoint = "https://theporndb.net/graphql";

    // Above CoveLibraryPort's 500-row keyset page: a single-page fixture passes even if the cursor never advances.
    private const int RowsSpanningTwoPages = 501;

    [Fact]
    public async Task StreamAllVideos_CrossesThePageBoundaryExactlyOnce()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            for (var i = 0; i < RowsSpanningTwoPages; i++)
            {
                db.Set<Video>().Add(new Video
                {
                    Title = $"Scene {i}",
                    RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = $"uuid-{i}" } },
                });
            }

            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var streamed = new List<CoveVideo>();
            await foreach (var video in port.StreamAllVideosAsync())
            {
                streamed.Add(video);
            }

            Assert.Equal(RowsSpanningTwoPages, streamed.Count);
            Assert.Equal(RowsSpanningTwoPages, streamed.Select(v => v.CoveId).Distinct().Count());
            Assert.Equal(streamed.Select(v => v.CoveId).OrderBy(id => id), streamed.Select(v => v.CoveId));

            // A second enumeration must visit the same rows in the same order: the pages are cut by keyset on
            // the ascending id, so two passes over an unchanged library cannot disagree.
            var second = new List<CoveVideo>();
            await foreach (var video in port.StreamAllVideosAsync())
            {
                second.Add(video);
            }

            Assert.Equal(second.Select(v => v.CoveId), streamed.Select(v => v.CoveId));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamAllVideos_ProjectsIdsAndPathsLikeTheMaterializedRead()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            db.Set<Video>().Add(new Video
            {
                Title = "Scene X",
                RemoteIds =
                {
                    new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-a" },
                    new VideoRemoteId { Endpoint = TpdbEndpoint, RemoteId = "tpdb-1" },
                },
                Files =
                {
                    new VideoFile
                    {
                        Basename = "x.mkv",
                        Path = "/mnt/tank/x.mkv",
                        ParentFolder = new Folder { Path = "/mnt/tank" },
                        Fingerprints = { new FileFingerprint { Type = "oshash", Value = "abc" } },
                    },
                },
            });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            CoveVideo? streamed = null;
            await foreach (var video in port.StreamAllVideosAsync())
            {
                streamed = video;
            }

            Assert.NotNull(streamed);
            Assert.Equal(["uuid-a"], streamed!.StashIds);
            Assert.Equal(["tpdb-1"], streamed.TpdbIds);
            Assert.Equal(["/mnt/tank/x.mkv"], streamed.FilePaths);
            Assert.Contains(streamed.Fingerprints, f => f.Type == "oshash" && f.Value == "abc");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamFilePaths_YieldsEveryFileAcrossThePageBoundary()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var folder = new Folder { Path = "/mnt/tank" };
            for (var i = 0; i < RowsSpanningTwoPages; i++)
            {
                db.Set<Video>().Add(new Video
                {
                    Title = $"Scene {i}",
                    Files =
                    {
                        new VideoFile
                        {
                            Basename = $"{i}.mkv",
                            Path = $"/mnt/tank/{i}.mkv",
                            ParentFolder = folder,
                        },
                    },
                });
            }

            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var paths = new List<string>();
            await foreach (var path in port.StreamFilePathsAsync())
            {
                paths.Add(path);
            }

            Assert.Equal(RowsSpanningTwoPages, paths.Count);
            Assert.Equal(RowsSpanningTwoPages, paths.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadOwnedRemoteIds_AnswersOnlyForTheAskedIdsAndFoldsCase()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            db.Set<Video>().Add(new Video
            {
                Title = "Owned",
                RemoteIds =
                {
                    new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "UUID-Owned" },
                    new VideoRemoteId { Endpoint = TpdbEndpoint, RemoteId = "tpdb-owned" },
                },
            });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);

            // Asked in a different casing than stored — a provider's casing need not match Cove's.
            var owned = await port.LoadOwnedRemoteIdsAsync(
                DiscoveryIdFamily.StashDb, ["uuid-owned", "uuid-absent"]);

            Assert.Contains("uuid-owned", owned);
            Assert.DoesNotContain("uuid-absent", owned);

            // The TPDB id is owned too, but never on the StashDB family — that separation is what keeps a v2 id
            // out of a v3 diff.
            Assert.DoesNotContain("tpdb-owned", owned);
            Assert.Contains("tpdb-owned", await port.LoadOwnedRemoteIdsAsync(DiscoveryIdFamily.Tpdb, ["TPDB-Owned"]));

            Assert.Empty(await port.LoadOwnedRemoteIdsAsync(DiscoveryIdFamily.StashDb, []));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
