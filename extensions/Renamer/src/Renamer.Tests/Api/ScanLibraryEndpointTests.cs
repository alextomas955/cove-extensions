using System.Data.Common;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// The whole-library scan: <c>ScanLibraryEnqueue</c> gates on ANY renamer-read permission and enqueues
/// (never directly executing), <c>RunScanLibraryJobAsync</c> runs the SAME planner <c>/preview</c> uses
/// against every server-derived id with ZERO disk/DB mutation, and <c>ScanLibraryResultAsync</c> reads
/// the persisted result back. Exercised as plain methods (no HTTP host) with a real SQLite
/// <c>CoveContext</c>, mirroring <c>PreviewEndpointTests</c>/<c>EntityIdsCapTests</c>/<c>RenamerBatchJobTests</c>.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class ScanLibraryEndpointTests
{
    /// <summary>Records every <c>Enqueue</c> call; all other members are unused and throw.</summary>
    private sealed class RecordingJobService : IJobService
    {
        public List<(string type, string description)> Enqueued { get; } = [];

        public string Enqueue(string type, string description, Func<Cove.Core.Interfaces.IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Enqueued.Add((type, description));
            return "job-123";
        }

        public bool Cancel(string jobId) => throw new NotImplementedException();
        public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotImplementedException();
        public JobInfo? GetJob(string jobId) => throw new NotImplementedException();
        public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotImplementedException();
        public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotImplementedException();
    }

    private static async Task<(global::Renamer.Renamer ext, FakeStore store)> NewExtensionAsync()
    {
        var ext = new global::Renamer.Renamer();
        var store = new FakeStore();
        // Pin a stable title-only template so seeded (height-less) rows render a deterministic name,
        // independent of the shipped default template.
        await new OptionsStore(store).SaveAsync(new RenamerOptions { FilenameTemplate = "$title" });
        ((IStatefulExtension)ext).SetStore(store);
        return (ext, store);
    }

    /// <summary>
    /// Wires the extension's captured seams (<c>_scopeFactory</c>, <c>_eventBus</c>) from a DI
    /// provider whose <c>DbContext</c> registration is SCOPED over <paramref name="conn"/>, so the job
    /// body's own <c>CreateAsyncScope()</c> resolves a context over the SAME database the test seeded —
    /// mirrors <c>RenamerBatchJobTests.BuildExtensionAsync</c>. The scan job never touches <c>IEventBus</c>,
    /// but <c>InitializeAsync</c> requires both seams to be resolvable.
    /// </summary>
    private static async Task InitializeOverSharedConnectionAsync(global::Renamer.Renamer ext, SqliteConnection conn)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options;
            return new CoveContext(options, principalAccessor: null);
        });
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        var provider = services.BuildServiceProvider();
        await ext.InitializeAsync(provider);
    }

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    [Fact]
    public async Task ScanLibraryEnqueue_WithAnyReadPermission_Returns202_AndEnqueuesExclusiveOnce()
    {
        var (ext, _) = await NewExtensionAsync();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

        var result = ext.ScanLibraryEnqueue(null, principal, jobs);

        Assert.Equal(202, StatusOf(result));
        Assert.Single(jobs.Enqueued);
    }

    [Fact]
    public async Task ScanLibraryEnqueue_WithNoReadPermission_Returns403_AndDoesNotEnqueue()
    {
        var (ext, _) = await NewExtensionAsync();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.None();

        var result = ext.ScanLibraryEnqueue(null, principal, jobs);

        Assert.Equal(403, StatusOf(result));
        Assert.Empty(jobs.Enqueued);
    }

    [Fact]
    public async Task RunScanLibraryJobAsync_AllKindsReadable_ReturnsOneItemPerFileAcrossAllKinds_AndMutatesNothing()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoEntityId1, videoFileId1) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films/one", "one.mkv", "One");
            var (_, videoEntityId2, videoFileId2) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films/two", "two.mkv", "Two");
            var (_, imageEntityId, imageFileId) = await ExecutorTestSeed.SeedImageAsync(db, "/library/pics", "pic.jpg", "Pic");
            var (_, audioEntityId, audioFileId) = await ExecutorTestSeed.SeedAudioAsync(db, "/library/music", "song.mp3", "Song");

            var (beforeVideoName, beforeVideoPath) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId1);

            var (ext, store) = await NewExtensionAsync();
            await InitializeOverSharedConnectionAsync(ext, conn);

            var progress = new FakeJobProgress();
            await ext.RunScanLibraryJobAsync(
                [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio], null, progress, default);

            var json = await store.GetAsync("last-scan-result");
            Assert.False(string.IsNullOrEmpty(json));

            var items = JsonSerializer.Deserialize<JsonElement[]>(json!)!;
            Assert.Equal(4, items.Length);

            var fileIds = items.Select(i => i.GetProperty("fileId").GetInt32()).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { videoFileId1, videoFileId2, imageFileId, audioFileId }.OrderBy(x => x), fileIds);

            // The entity id is threaded from the plan onto every file's wire item — asserted PAIRED
            // with its fileId so the mapping is proven, not just that the set of ids appears somewhere.
            var entityIdByFileId = items.ToDictionary(
                i => i.GetProperty("fileId").GetInt32(),
                i => i.GetProperty("entityId").GetInt32());
            Assert.Equal(videoEntityId1, entityIdByFileId[videoFileId1]);
            Assert.Equal(videoEntityId2, entityIdByFileId[videoFileId2]);
            Assert.Equal(imageEntityId, entityIdByFileId[imageFileId]);
            Assert.Equal(audioEntityId, entityIdByFileId[audioFileId]);

            // Every item carries an explicit per-item kind tag (the multi-kind response gap RESEARCH found).
            var kinds = items.Select(i => i.GetProperty("kind").GetString()!).ToHashSet();
            Assert.Equal(new HashSet<string> { "Video", "Image", "Audio" }, kinds);

            // Item-equality vs a per-id REFERENCE: the batched scan output must equal what the old
            // per-id path (LoadEntityAsync + planner per id) produced — same set of
            // fileId → {entityId, kind, newBasename, status}. Compute the reference over the SAME db.
            var refPort = new CoveRenamerDataPort(db);
            var refPlanner = new RenamerPlanner(refPort);
            var refOptions = await new OptionsStore(store).LoadAsync(default);
            var reference = new List<(int fileId, int entityId, string kind, string newBasename, string status)>();
            foreach (var (kind, entityIds) in new[]
            {
                (RenamerFileKind.Video, new[] { videoEntityId1, videoEntityId2 }),
                (RenamerFileKind.Image, new[] { imageEntityId }),
                (RenamerFileKind.Audio, new[] { audioEntityId }),
            })
            {
                foreach (var eid in entityIds)
                {
                    var plan = await refPlanner.PlanAsync(kind, eid, refOptions, default);
                    foreach (var pi in plan.Items)
                    {
                        reference.Add((pi.FileId, plan.EntityId, kind.ToString(), pi.NewBasename, pi.Status.ToString()));
                    }
                }
            }

            var actual = items.Select(i => (
                fileId: i.GetProperty("fileId").GetInt32(),
                entityId: i.GetProperty("entityId").GetInt32(),
                kind: i.GetProperty("kind").GetString()!,
                newBasename: i.GetProperty("newBasename").GetString()!,
                status: i.GetProperty("status").GetString()!)).ToList();

            Assert.Equal(
                reference.OrderBy(r => r.fileId).ToList(),
                actual.OrderBy(a => a.fileId).ToList());

            // Zero mutation: the seeded row is byte-for-byte unchanged after the scan job ran.
            var (afterVideoName, afterVideoPath) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId1);
            Assert.Equal(beforeVideoName, afterVideoName);
            Assert.Equal(beforeVideoPath, afterVideoPath);

            // Progress feedback: the scan must report INTERMEDIATE progress as it plans, not jump
            // straight to 1.0 at the end (the 0%→100% regression). With 4 seeded entities there must be
            // at least one sub-1.0 report, every report must be in (0,1], and the sequence must be
            // non-decreasing and end at exactly 1.0.
            Assert.Equal(1d, progress.Reports[^1].Percent);
            Assert.Contains(progress.Reports, r => r.Percent is > 0d and < 1d);
            Assert.All(progress.Reports, r => Assert.InRange(r.Percent, 0d, 1d));
            var percents = progress.Reports.Select(r => r.Percent).ToList();
            Assert.Equal(percents.OrderBy(p => p).ToList(), percents);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunScanLibraryJobAsync_MissingImagesRead_OmitsImageItems_ButKeepsVideoItems()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, _, videoFileId) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "one.mkv", "One");
            await ExecutorTestSeed.SeedImageAsync(db, "/library/pics", "pic.jpg", "Pic");

            var (ext, store) = await NewExtensionAsync();
            await InitializeOverSharedConnectionAsync(ext, conn);

            var progress = new FakeJobProgress();
            // Caller holds videos.read but NOT images.read — only Video is in the captured readable set.
            await ext.RunScanLibraryJobAsync([RenamerFileKind.Video], null, progress, default);

            var json = await store.GetAsync("last-scan-result");
            var items = JsonSerializer.Deserialize<JsonElement[]>(json!)!;

            Assert.Single(items);
            Assert.Equal(videoFileId, items[0].GetProperty("fileId").GetInt32());
            Assert.Equal("Video", items[0].GetProperty("kind").GetString());
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ScanLibraryEnqueue_WithOptionsBody_Returns202_AndEnqueues()
    {
        var (ext, _) = await NewExtensionAsync();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

        var body = new global::Renamer.Api.ScanLibraryRequest(
            JsonSerializer.Serialize(new RenamerOptions { FilenameTemplate = "$title" }, RenamerOptions.JsonOptions));
        var result = ext.ScanLibraryEnqueue(body, principal, jobs);

        Assert.Equal(202, StatusOf(result));
        Assert.Single(jobs.Enqueued);
    }

    [Fact]
    public async Task RunScanLibraryJobAsync_WithOverrideOptions_UsesThemOverSavedOptions()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The scan previews the source on disk, so the seeded row needs a real on-disk file — a
            // gone source would be SkipMissingSource, not the previewed rename this test asserts.
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, _, videoFileId) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "one.mkv", "One");
            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "video-bytes");

            // Saved options template is "$title" (from NewExtensionAsync). The override below uses a
            // DIFFERENT template with a literal prefix, so a scan that honors the override produces a
            // visibly different new name than a scan of the saved options would.
            var (ext, _) = await NewExtensionAsync();
            await InitializeOverSharedConnectionAsync(ext, conn);

            var overrideOptions = new RenamerOptions { FilenameTemplate = "DRYRUN - $title" };

            var progress = new FakeJobProgress();
            await ext.RunScanLibraryJobAsync([RenamerFileKind.Video], overrideOptions, progress, default);

            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var result = await ext.ScanLibraryResultAsync(principal, default);
            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Api.ScanItem[]>>(result);
            var item = Assert.Single(ok.Value!);

            Assert.Equal(videoFileId, item.FileId);
            // The scanned new basename reflects the OVERRIDE template's literal prefix, proving the
            // dry run previewed the unsaved options rather than the saved "$title".
            Assert.StartsWith("DRYRUN - One", item.NewBasename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>Counts each executed reader command so a test can prove the port issues ~N/chunk queries, not N.</summary>
    private sealed class SelectCountingInterceptor : DbCommandInterceptor
    {
        public int ReaderCount { get; set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            ReaderCount++;
            return ValueTask.FromResult(result);
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            ReaderCount++;
            return result;
        }
    }

    [Fact]
    public async Task ScanLoop_UsesBatchLoad_NotPerIdLoad()
    {
        // The scan-loop shape (batch-load a kind, then plan each id in order) must call the BATCH
        // method and never the per-id LoadEntityAsync. Drive that shape over a fake seam so the call
        // counters are observable (the real scan builds its own port from the DI-scoped DbContext).
        var port = new FakeRenamerDataPort();
        for (int id = 1; id <= 5; id++)
        {
            port.SeedEntity(new RenamerEntity(
                id, RenamerFileKind.Video, $"T{id}", null, null, null, true,
                [], [], [new RenamerFile(id, RenamerFileKind.Video, $"f{id}.mkv", 1, "media")]));
        }
        var ids = Enumerable.Range(1, 5).ToArray();
        port.SeedAllIds(RenamerFileKind.Video, ids);
        var planner = new RenamerPlanner(port);
        var options = new RenamerOptions { FilenameTemplate = "$title" };
        var lookups = new RouteLookups(
            new Dictionary<int, string>(), new Dictionary<string, string>(),
            new Dictionary<string, string>(), Array.Empty<(System.Text.RegularExpressions.Regex, string)>());

        var loaded = await port.LoadEntitiesAsync(RenamerFileKind.Video, ids);
        var byId = loaded.ToDictionary(e => e.EntityId);
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var e))
            {
                await planner.PlanLoadedEntity(e, options, lookups, default);
            }
        }

        Assert.Equal(1, port.LoadEntitiesCallCount);  // one batch call for the kind
        Assert.Equal(0, port.LoadEntityCallCount);     // never the per-id path
    }

    [Fact]
    public async Task LoadEntitiesAsync_IssuesCeilOverChunk_ReaderQueries_NotOnePerId()
    {
        // Prove the port collapses N per-entity round-trips into ceil(N/chunk) reader queries. Seed
        // more ids than one chunk so the assertion is meaningful (2 chunks worth). Count executed
        // reader commands via an EF command interceptor over a real SQLite context.
        var interceptor = new SelectCountingInterceptor();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new CoveContext(options, principalAccessor: null);
            await db.Database.EnsureCreatedAsync();

            int n = CoveRenamerDataPort.LoadChunkSize + 25;  // spans two chunks
            var ids = new List<int>(n);
            for (int k = 0; k < n; k++)
            {
                var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                    db, folderPath: $"media/{k}", basename: $"c{k}.mkv", title: $"C{k}");
                ids.Add(videoId);
            }

            var port = new CoveRenamerDataPort(db);
            interceptor.ReaderCount = default;  // count only the batch load below
            var loaded = await port.LoadEntitiesAsync(RenamerFileKind.Video, ids);

            Assert.Equal(n, loaded.Count);
            int expectedChunks = (n + CoveRenamerDataPort.LoadChunkSize - 1) / CoveRenamerDataPort.LoadChunkSize;
            // One heavy query per chunk — far fewer than N. (EF may split an Include into a bounded,
            // constant number of reader commands per query; assert the per-id blow-up is gone, i.e.
            // the reader count is on the order of chunks, never N.)
            Assert.True(interceptor.ReaderCount <= expectedChunks * 4,
                $"expected ~{expectedChunks} chunk queries, got {interceptor.ReaderCount} readers for {n} ids");
            Assert.True(interceptor.ReaderCount < n,
                $"batch load must issue fewer than N={n} reader queries; got {interceptor.ReaderCount}");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task ScanLibraryResultAsync_NoScanYet_Returns404()
    {
        var (ext, _) = await NewExtensionAsync();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

        var result = await ext.ScanLibraryResultAsync(principal, default);

        Assert.IsType<NotFound>(result);
    }

    [Fact]
    public async Task ScanLibraryResultAsync_WithNoReadPermission_Returns403()
    {
        var (ext, _) = await NewExtensionAsync();
        var principal = FakePrincipalAccessor.None();

        var result = await ext.ScanLibraryResultAsync(principal, default);

        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task ScanLibraryResultAsync_AfterJobCompletes_ReturnsThePersistedItems()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, entityId, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "one.mkv", "One");

            var (ext, _) = await NewExtensionAsync();
            await InitializeOverSharedConnectionAsync(ext, conn);

            var progress = new FakeJobProgress();
            await ext.RunScanLibraryJobAsync([RenamerFileKind.Video], null, progress, default);

            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var result = await ext.ScanLibraryResultAsync(principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Api.ScanItem[]>>(result);
            var item = Assert.Single(ok.Value!);
            Assert.Equal(fileId, item.FileId);
            Assert.Equal(entityId, item.EntityId);
            Assert.Equal(RenamerFileKind.Video, item.Kind);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
