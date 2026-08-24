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
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// The whole-library scan: <c>ScanLibraryEnqueue</c> gates on ANY renamer-read permission and enqueues
/// (never directly executing), <c>RunScanLibraryJobAsync</c> runs the SAME planner <c>/preview</c> uses
/// against every server-derived id with ZERO disk/DB mutation, <c>ScanLibraryResultAsync</c> reads the
/// persisted aggregate back per readable kind, <c>ScanRowsAsync</c> serves the rows a page at a time, and
/// <c>InitializeAsync</c> purges the pre-0.2.1 per-file scan value. Exercised as plain methods (no HTTP
/// host) with a real SQLite <c>CoveContext</c>, mirroring
/// <c>PreviewEndpointTests</c>/<c>EntityIdsCapTests</c>/<c>RenamerBatchJobTests</c>.
/// </summary>
[Trait("Tier", "L1")]
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

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

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
    public async Task RunScanLibraryJobAsync_AllKindsReadable_AggregatesEveryFileAcrossAllKinds_AndMutatesNothing()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, _, videoFileId1) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films/one", "one.mkv", "One");
            var (_, _, videoFileId2) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films/two", "two.mkv", "Two");
            await ExecutorTestSeed.SeedImageAsync(db, "/library/pics", "pic.jpg", "Pic");
            await ExecutorTestSeed.SeedAudioAsync(db, "/library/music", "song.mp3", "Song");

            var (beforeVideoName, beforeVideoPath) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId1);

            var (ext, store) = await NewExtensionAsync();
            await InitializeOverSharedConnectionAsync(ext, conn);

            var progress = new FakeJobProgress();
            await ext.RunScanLibraryJobAsync(
                [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio], null, progress, default);

            var json = await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey);
            Assert.False(string.IsNullOrEmpty(json));

            var summary = JsonSerializer.Deserialize<global::Renamer.Contracts.ScanSummary>(json!, EnumJson)!;

            // Per kind, not flat — that split is what lets the readback drop a kind the caller cannot see.
            Assert.Equal(
                new HashSet<RenamerFileKind> { RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio },
                summary.Kinds.Select(k => k.Kind).ToHashSet());
            Assert.Equal(4, summary.Kinds.Sum(k => k.Files));
            Assert.Equal(4, summary.Kinds.Sum(k => k.Entities));
            // Exact, not sampled: every kind's per-status counts account for all of its files.
            Assert.All(summary.Kinds, k => Assert.Equal(k.Files, k.StatusCounts.Sum(c => c.Count)));

            // The rows themselves come from the page query, planned on demand — every seeded file appears.
            var principal = FakePrincipalAccessor.WithPermissions(
                Permissions.VideosRead, Permissions.ImagesRead, Permissions.AudiosRead);
            var page = await ReadRowsAsync(ext, principal);
            Assert.Equal(4, page.Rows.Count);
            Assert.Contains(videoFileId1, page.Rows.Select(r => r.FileId));
            Assert.Contains(videoFileId2, page.Rows.Select(r => r.FileId));
            Assert.Equal(
                new HashSet<RenamerFileKind> { RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio },
                page.Rows.Select(r => r.Kind).ToHashSet());
            Assert.Null(page.Next);

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

            var json = await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey);
            var summary = JsonSerializer.Deserialize<global::Renamer.Contracts.ScanSummary>(json!, EnumJson)!;

            var kind = Assert.Single(summary.Kinds);
            Assert.Equal(RenamerFileKind.Video, kind.Kind);
            Assert.Equal(1, kind.Files);

            // The rows page applies the same per-kind gate: a videos-only caller never sees the image row.
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var page = await ReadRowsAsync(ext, principal);
            Assert.Equal(videoFileId, Assert.Single(page.Rows).FileId);
            Assert.Equal(RenamerFileKind.Video, page.Rows[0].Kind);
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
            var summary = await ReadSummaryAsync(ext, principal);
            Assert.Equal(1, summary.WillChange);

            // The page plans with the options the CALLER sends, so passing the same override reproduces
            // the scanned name: the literal prefix proves the unsaved options were previewed, not "$title".
            var page = await ReadRowsAsync(ext, principal, new global::Renamer.Contracts.ScanRowsRequest(
                Options: JsonSerializer.Serialize(overrideOptions, RenamerOptions.JsonOptions),
                Kind: null, AfterEntityId: null, Take: null, Query: null, Bucket: null));
            var row = Assert.Single(page.Rows);
            Assert.Equal(videoFileId, row.FileId);
            Assert.Contains("DRYRUN - One", row.NewFullPath, StringComparison.Ordinal);
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

        Assert.IsType<NotFound>(Unwrap(result));
    }

    [Fact]
    public async Task ScanLibraryResultAsync_WithNoReadPermission_Returns403()
    {
        var (ext, _) = await NewExtensionAsync();
        var principal = FakePrincipalAccessor.None();

        var result = await ext.ScanLibraryResultAsync(principal, default);

        Assert.Equal(403, StatusOf(result));
    }

    /// <summary>Serializes/reads the stored scan aggregate with the wire's camelCase + string enums.</summary>
    private static readonly JsonSerializerOptions EnumJson =
        new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Invokes the readback and unwraps the merged view.</summary>
    private static async Task<global::Renamer.Contracts.ScanSummaryView> ReadSummaryAsync(
        global::Renamer.Renamer ext, ICurrentPrincipalAccessor principal)
    {
        var result = await ext.ScanLibraryResultAsync(principal, default);
        return Assert.IsType<Ok<global::Renamer.Contracts.ScanSummaryView>>(Unwrap(result)).Value!;
    }

    /// <summary>Invokes the page query and unwraps the page.</summary>
    private static async Task<global::Renamer.Contracts.ScanRowsPage> ReadRowsAsync(
        global::Renamer.Renamer ext, ICurrentPrincipalAccessor principal,
        global::Renamer.Contracts.ScanRowsRequest? body = null)
    {
        var result = await ext.ScanRowsAsync(body, principal, default);
        return Assert.IsType<Ok<global::Renamer.Contracts.ScanRowsPage>>(Unwrap(result)).Value!;
    }

    /// <summary>A one-kind aggregate whose per-status counts are the only thing the readback merges.</summary>
    private static global::Renamer.Contracts.ScanKindSummary MakeKind(
        RenamerFileKind kind, int files, RenamerStatus status) =>
        new(kind, Entities: files, Files: files,
            StatusCounts: [.. Enum.GetValues<RenamerStatus>()
                .Select(s => new global::Renamer.Contracts.ScanStatusCount(s, s == status ? files : 0))],
            BlastRadius: new PreviewSummary(files, files, 0, 0, [], ConfirmLevel.Light, Undoable: true),
            VolumePairsTruncated: false);

    private static Task StoreSummaryAsync(FakeStore store, params global::Renamer.Contracts.ScanKindSummary[] kinds)
        => store.SetAsync(
            global::Renamer.Renamer.LastScanSummaryKey,
            JsonSerializer.Serialize(
                new global::Renamer.Contracts.ScanSummary(
                    global::Renamer.Contracts.ScanSummary.CurrentSchemaVersion, 42L, kinds),
                EnumJson));

    [Fact]
    public async Task ScanLibraryResultAsync_VideoOnlyCaller_ReturnsOnlyVideoFigures_NotImageOrAudio()
    {
        // A higher-permission user's scan persisted Video+Image+Audio figures under the FIXED key. A
        // video-only caller reading it back must NOT receive the image/audio counts (the cross-kind leak).
        var (ext, store) = await NewExtensionAsync();
        await StoreSummaryAsync(store,
            MakeKind(RenamerFileKind.Video, 3, RenamerStatus.Renamer),
            MakeKind(RenamerFileKind.Image, 5, RenamerStatus.Renamer),
            MakeKind(RenamerFileKind.Audio, 7, RenamerStatus.NoOp));

        var view = await ReadSummaryAsync(ext, FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        Assert.Equal([RenamerFileKind.Video], view.Kinds);
        Assert.Equal(3, view.TotalFiles);
        Assert.Equal(3, view.WillChange);
        Assert.Equal(0, view.NoChange);
    }

    [Fact]
    public async Task ScanLibraryResultAsync_AllKindReader_SeesEveryKindsFigures_MergedIntoBuckets()
    {
        var (ext, store) = await NewExtensionAsync();
        await StoreSummaryAsync(store,
            MakeKind(RenamerFileKind.Video, 3, RenamerStatus.Renamer),
            MakeKind(RenamerFileKind.Image, 5, RenamerStatus.SkipGated),
            MakeKind(RenamerFileKind.Audio, 7, RenamerStatus.NoOp));

        var view = await ReadSummaryAsync(ext, FakePrincipalAccessor.WithPermissions(
            Permissions.VideosRead, Permissions.ImagesRead, Permissions.AudiosRead));

        Assert.Equal(15, view.TotalFiles);
        Assert.Equal(3, view.WillChange);
        Assert.Equal(5, view.Attention);
        Assert.Equal(7, view.NoChange);
        Assert.Equal(view.TotalFiles, view.StatusCounts.Sum(c => c.Count));
        Assert.Equal(42L, view.CompletedAtUtcTicks);
    }

    [Fact]
    public async Task ScanLibraryResultAsync_UnparseableOrUnknownSchema_Reads404_NotA500()
    {
        var (ext, store) = await NewExtensionAsync();

        await store.SetAsync(global::Renamer.Renamer.LastScanSummaryKey, "{not json");
        Assert.IsType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), default)));

        await store.SetAsync(
            global::Renamer.Renamer.LastScanSummaryKey,
            JsonSerializer.Serialize(
                new global::Renamer.Contracts.ScanSummary(
                    global::Renamer.Contracts.ScanSummary.CurrentSchemaVersion + 1, 0L, []),
                EnumJson));
        Assert.IsType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), default)));
    }

    [Fact]
    public async Task ScanRowsAsync_WithNoReadPermission_Returns403()
    {
        var (ext, _) = await NewExtensionAsync();

        var result = await ext.ScanRowsAsync(null, FakePrincipalAccessor.None(), default);

        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task ScanRowsAsync_UnknownKindOrBucket_Returns400()
    {
        var (ext, _) = await NewExtensionAsync();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

        Assert.Equal(400, StatusOf(await ext.ScanRowsAsync(
            new global::Renamer.Contracts.ScanRowsRequest(null, "gallery", null, null, null, null), principal, default)));
        Assert.Equal(400, StatusOf(await ext.ScanRowsAsync(
            new global::Renamer.Contracts.ScanRowsRequest(null, null, null, null, null, "nonsense"), principal, default)));
    }

    [Fact]
    public async Task ScanLibraryResultAsync_AfterJobCompletes_ReturnsTheExactAggregate()
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
            var view = await ReadSummaryAsync(ext, principal);

            Assert.Equal(1, view.TotalFiles);
            Assert.Equal(1, view.TotalEntities);
            Assert.Equal([RenamerFileKind.Video], view.Kinds);
            Assert.Equal(view.TotalFiles, view.StatusCounts.Sum(c => c.Count));

            var row = Assert.Single((await ReadRowsAsync(ext, principal)).Rows);
            Assert.Equal(fileId, row.FileId);
            Assert.Equal(entityId, row.EntityId);
            Assert.Equal(RenamerFileKind.Video, row.Kind);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // ── The pre-0.2.1 per-file scan value: purged at load, never read ──────────

    /// <summary>An <c>IExtensionStore</c> whose delete always throws, to drive the purge's containment.</summary>
    private sealed class ThrowingDeleteStore : Cove.Plugins.IExtensionStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken ct = default)
            => throw new InvalidOperationException("store unavailable");
        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, string>());
    }

    private static async Task<global::Renamer.Renamer> InitializeWithStoreAsync(Cove.Plugins.IExtensionStore store)
    {
        // A database carrying the journal and nothing else: the extension refuses to load without a
        // readable journal, and these tests are about what load does to the STORE.
        await using var journalDb = await JournalOnlyDatabase.CreateAsync();
        var ext = new global::Renamer.Renamer();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(journalDb.BuildProvider());
        return ext;
    }

    [Fact]
    public async Task InitializeAsync_WithALegacyScanValue_DeletesIt_WithoutEverReadingIt()
    {
        var store = new FakeStore();
        await store.SetAsync(global::Renamer.Renamer.LastScanResultKey, "[a legacy per-file array]");
        store.GetKeys.Clear();

        await InitializeWithStoreAsync(store);

        // Reading the value to decide whether to delete it is the one operation guaranteed to hurt: the
        // host's bulk read already fails on it, and its own delete materializes the row it removes.
        Assert.DoesNotContain(global::Renamer.Renamer.LastScanResultKey, store.GetKeys);
        Assert.Null(await store.GetAsync(global::Renamer.Renamer.LastScanResultKey));
    }

    [Fact]
    public async Task InitializeAsync_WithNoLegacyScanValue_CompletesAndWritesNothing()
    {
        var store = new FakeStore();
        store.GetKeys.Clear();
        int setsBefore = store.SetCallCount;

        await InitializeWithStoreAsync(store);

        Assert.Equal(setsBefore, store.SetCallCount);
        Assert.Null(await store.GetAsync(global::Renamer.Renamer.LastScanResultKey));
    }

    [Fact]
    public async Task InitializeAsync_LeavesAPreExistingScanSummaryUntouched()
    {
        var store = new FakeStore();
        await StoreSummaryAsync(store, MakeKind(RenamerFileKind.Video, 2, RenamerStatus.Renamer));
        await store.SetAsync(global::Renamer.Renamer.LastScanResultKey, "[legacy]");
        string before = (await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey))!;

        await InitializeWithStoreAsync(store);

        Assert.Equal(before, await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey));
        Assert.Null(await store.GetAsync(global::Renamer.Renamer.LastScanResultKey));
    }

    [Fact]
    public async Task InitializeAsync_WhenTheDeleteThrows_StillCompletes()
    {
        // A load that refuses to finish because the cleanup failed leaves the user strictly worse off.
        var ext = await InitializeWithStoreAsync(new ThrowingDeleteStore());

        Assert.Equal("com.alextomas955.renamer", ext.Id);
    }
}
