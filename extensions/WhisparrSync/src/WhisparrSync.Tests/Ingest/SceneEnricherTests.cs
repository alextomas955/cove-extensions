using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Ingest;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Ingest;

/// <summary>
/// The post-ingest enrichment contract (<see cref="SceneEnricher"/>) over an in-memory CoveContext: a
/// genuine import stamps the source id (idempotently), best-effort identifies by it through
/// <see cref="IMetadataServerService"/>, and kicks a scan/generate pass — and a failed/absent metadata
/// server never breaks the guaranteed stamp.
/// </summary>
public sealed class SceneEnricherTests
{
    private const string StashEndpoint = "https://stashdb.org/graphql";
    private static readonly SceneIdentity Identity = new("019f3391-e257-7dd7-8c79-e399f2ad6ca5", StashEndpoint);

    private sealed class FakeMetadataServer : IMetadataServerService
    {
        public List<(string Endpoint, string VideoId)> Calls { get; } = [];
        public bool Result { get; init; } = true;
        public Exception? Throws { get; init; }

        public Task<bool> MergeVideoAsync(Video video, string endpoint, string videoId, MetadataServerVideoImportRequestDto? importConfig, CancellationToken ct)
        {
            if (Throws is { } ex)
            {
                throw ex;
            }

            Calls.Add((endpoint, videoId));
            return Task.FromResult(Result);
        }
    }

    private static async Task<int> SeedBareVideoAsync(Cove.Data.CoveContext db)
    {
        var video = new Video { Title = "raw import" };
        db.Set<Video>().Add(video);
        await db.SaveChangesAsync();
        return video.Id;
    }

    private static Video Reload(Cove.Data.CoveContext db, int coveId)
        => db.Set<Video>().Include(v => v.RemoteIds).AsNoTracking().Single(v => v.Id == coveId);

    [Fact]
    public async Task Enrich_StampsRemoteId_Identifies_AndTriggersScanGenerate()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var coveId = await SeedBareVideoAsync(db);
            var meta = new FakeMetadataServer();
            var scan = new FakeScanService();

            await SceneEnricher.EnrichAsync(db, meta, scan, coveId, Identity, "/media/scene.mp4", default);

            var stamped = Assert.Single(Reload(db, coveId).RemoteIds);
            Assert.Equal(StashEndpoint, stamped.Endpoint);
            Assert.Equal(Identity.RemoteId, stamped.RemoteId);

            Assert.Equal((StashEndpoint, Identity.RemoteId), Assert.Single(meta.Calls));

            var options = Assert.Single(scan.Scans);
            Assert.Equal(["/media/scene.mp4"], options.Paths);
            Assert.True(options.Rescan && options.GenerateCovers && options.GeneratePreviews);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enrich_AlreadyIdentified_IsNoOp_IdentifyAndScanRunExactlyOnce()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var coveId = await SeedBareVideoAsync(db);
            var meta = new FakeMetadataServer();
            var scan = new FakeScanService();

            // First ingest identifies + scans; a re-ingest of the SAME scene (redelivery / upgrade / reconcile
            // overlap) sees the stamp and must do nothing — no re-fetch (which would clobber edits), no re-scan.
            await SceneEnricher.EnrichAsync(db, meta, scan, coveId, Identity, "/media/s.mp4", default);
            await SceneEnricher.EnrichAsync(db, meta, scan, coveId, Identity, "/media/s.mp4", default);

            Assert.Single(Reload(db, coveId).RemoteIds); // one stamp, not two
            Assert.Single(meta.Calls); // identified exactly once
            Assert.Single(scan.Scans); // generated exactly once
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enrich_MetadataServerThrows_StillStampsAndScans()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var coveId = await SeedBareVideoAsync(db);
            var scan = new FakeScanService();
            // No configured box → MergeVideoAsync throws; the stamp + scan must still happen (best-effort identify).
            var meta = new FakeMetadataServer { Throws = new InvalidOperationException("no box") };

            await SceneEnricher.EnrichAsync(db, meta, scan, coveId, Identity, "/media/s.mp4", default);

            Assert.Single(Reload(db, coveId).RemoteIds);
            Assert.Single(scan.Scans);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
