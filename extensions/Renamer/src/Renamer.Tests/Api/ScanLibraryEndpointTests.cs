using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
            var (_, _, videoFileId1) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films/one", "one.mkv", "One");
            var (_, _, videoFileId2) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films/two", "two.mkv", "Two");
            var (_, _, imageFileId) = await ExecutorTestSeed.SeedImageAsync(db, "/library/pics", "pic.jpg", "Pic");
            var (_, _, audioFileId) = await ExecutorTestSeed.SeedAudioAsync(db, "/library/music", "song.mp3", "Song");

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

            // Every item carries an explicit per-item kind tag (the multi-kind response gap RESEARCH found).
            var kinds = items.Select(i => i.GetProperty("kind").GetString()!).ToHashSet();
            Assert.Equal(new HashSet<string> { "Video", "Image", "Audio" }, kinds);

            // Zero mutation: the seeded row is byte-for-byte unchanged after the scan job ran.
            var (afterVideoName, afterVideoPath) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId1);
            Assert.Equal(beforeVideoName, afterVideoName);
            Assert.Equal(beforeVideoPath, afterVideoPath);

            Assert.Contains(progress.Reports, r => r.Percent == 1d);
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
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, _, videoFileId) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "one.mkv", "One");

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
            var (_, _, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "one.mkv", "One");

            var (ext, _) = await NewExtensionAsync();
            await InitializeOverSharedConnectionAsync(ext, conn);

            var progress = new FakeJobProgress();
            await ext.RunScanLibraryJobAsync([RenamerFileKind.Video], null, progress, default);

            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var result = await ext.ScanLibraryResultAsync(principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Api.ScanItem[]>>(result);
            var item = Assert.Single(ok.Value!);
            Assert.Equal(fileId, item.FileId);
            Assert.Equal(RenamerFileKind.Video, item.Kind);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
