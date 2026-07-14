using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Client;
using WhisparrSync.Ingest;
using WhisparrSync.Reconcile;
using WhisparrSync.State;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// IMPT-02 backstop contract for <see cref="ReconcileJob"/> over a fabricated paged history envelope
/// (a real <see cref="WhisparrClient"/> on <see cref="FakeHttpMessageHandler"/> + <see cref="FakeStore"/> +
/// <see cref="FakeScanService"/>): the reconcile ingests only the import-type records, dedups a
/// webhook-then-poll overlap on the SHARED <see cref="EventLedger.ImportKey"/> so the same import ingests
/// once across channels, advances the checkpoint so a re-run is incremental, and — on the first run with no
/// stored checkpoint — seeds at "now" so the whole prior history is never retro-ingested.
/// </summary>
public sealed class ReconcileTests
{
    private const string Root = "/data/media";

    // A newest-first history page (records 5,4,3): two downloadFolderImported rows around a non-import row.
    private const string MixedHistory = """
        {
          "page": 1, "pageSize": 50, "totalRecords": 3,
          "records": [
            { "id": 5, "movieId": 1, "eventType": "downloadFolderImported",
              "data": { "importedPath": "/data/media/A/A.mkv", "downloadId": "dl-a" } },
            { "id": 4, "movieId": 2, "eventType": "grabbed",
              "data": { "downloadId": "dl-b" } },
            { "id": 3, "movieId": 3, "eventType": "downloadFolderImported",
              "data": { "importedPath": "/data/media/C/C.mkv", "downloadId": "dl-c" } }
          ]
        }
        """;

    private static (ReconcileJob Job, FakeScanService Scan, FakeStore Store) NewJob(string historyJson)
    {
        var scan = new FakeScanService();
        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => scan);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var coordinator = new IngestCoordinator(
            scopeFactory, _ => ValueTask.FromResult<IReadOnlyList<string>>([Root]));
        var store = new FakeStore();
        var client = new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json(historyJson)));
        var job = new ReconcileJob(
            store, coordinator, (page, c) => client.ListHistoryAsync("http://w", "k", page, ReconcileJob.PageSize, c));
        return (job, scan, store);
    }

    // A checkpoint seeded at record 0 so every fixture record (id > 0) is a NEW record to process.
    private static async Task SeedAtZeroAsync(FakeStore store)
        => await new Checkpoint(store).SaveAsync(new CheckpointState(0, DateTime.UtcNow.Ticks, Seeded: true));

    [Fact]
    public async Task FeedsOnlyImportEvents_IngestingEachNewImportOnce()
    {
        var (job, scan, store) = NewJob(MixedHistory);
        await SeedAtZeroAsync(store);

        await job.RunAsync(default);

        // Only the two downloadFolderImported rows ingested (the "grabbed" row is skipped).
        Assert.Equal(2, scan.Imports.Count);
        Assert.Contains(scan.Imports, i => i.Path == "/data/media/A/A.mkv");
        Assert.Contains(scan.Imports, i => i.Path == "/data/media/C/C.mkv");
        Assert.DoesNotContain(scan.Imports, i => i.Path.Contains("dl-b"));
    }

    [Fact]
    public async Task WebhookThenPollOverlap_IsIngestedOnce_ViaTheSharedLedgerKey()
    {
        const string history = """
            {
              "page": 1, "pageSize": 50, "totalRecords": 1,
              "records": [
                { "id": 7, "movieId": 1, "eventType": "downloadFolderImported",
                  "data": { "importedPath": "/data/media/A/A.mkv", "downloadId": "dl-a" } }
              ]
            }
            """;
        var (job, scan, store) = NewJob(history);
        await SeedAtZeroAsync(store);

        // Simulate the webhook already having ingested this exact import: seed the SHARED cross-channel key.
        await new EventLedger(store).RecordAsync(EventLedger.ImportKey("dl-a", "/data/media/A/A.mkv"));

        await job.RunAsync(default);

        // The poll derives the identical key, sees it, and ingests nothing — one import across both channels.
        Assert.Empty(scan.Imports);
    }

    [Fact]
    public async Task AdvancesCheckpointToNewestRecord_ThenReRunIngestsNothing()
    {
        var (job, scan, store) = NewJob(MixedHistory);
        await SeedAtZeroAsync(store);

        await job.RunAsync(default);

        var afterFirst = await new Checkpoint(store).LoadAsync();
        Assert.Equal(5, afterFirst.LastRecordId); // advanced to the newest record id (regardless of event type)
        Assert.Equal(2, scan.Imports.Count);

        // A re-run over the same (unchanged) history finds nothing above the checkpoint.
        await job.RunAsync(default);
        Assert.Equal(2, scan.Imports.Count);
    }

    [Fact]
    public async Task FirstRun_NoCheckpoint_SeedsAtNewest_AndDoesNotRetroIngest()
    {
        var (job, scan, store) = NewJob(MixedHistory);
        // No checkpoint seeded → the first run must seed, not ingest.

        await job.RunAsync(default);

        Assert.Empty(scan.Imports); // the whole prior history is NOT retro-ingested
        var seeded = await new Checkpoint(store).LoadAsync();
        Assert.True(seeded.Seeded);
        Assert.Equal(5, seeded.LastRecordId); // seeded at the newest existing record
    }

    [Fact]
    public async Task ResolvesDroppedPath_WhenImportedPathAbsent()
    {
        const string history = """
            {
              "page": 1, "pageSize": 50, "totalRecords": 1,
              "records": [
                { "id": 9, "movieId": 1, "eventType": "downloadFolderImported",
                  "data": { "droppedPath": "/data/media/D/D.mkv", "downloadId": "dl-d" } }
              ]
            }
            """;
        var (job, scan, store) = NewJob(history);
        await SeedAtZeroAsync(store);

        await job.RunAsync(default);

        Assert.Equal("/data/media/D/D.mkv", Assert.Single(scan.Imports).Path);
    }
}
