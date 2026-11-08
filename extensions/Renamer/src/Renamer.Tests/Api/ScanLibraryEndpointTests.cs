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
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

public sealed class ScanLibraryEndpointTests
{
    private static async Task<(global::Renamer.Renamer ext, FakeStore store)> NewExtensionAsync()
    {
        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        // Pin a stable title-only template so seeded (height-less) rows render a deterministic name,
        // independent of the shipped default template.
        await new OptionsStore(store).SaveAsync(new RenamerOptions { FilenameTemplate = "$title" });
        ((IStatefulExtension)ext).SetStore(store);
        return (ext, store);
    }

    // Wires the extension's captured seams from a DI provider whose DbContext registration is scoped
    // over conn, so the job body's own scope resolves a context over the same database the test
    // seeded. The scan job never touches IEventBus, but InitializeAsync requires both seams to be
    // resolvable.
    private static async Task InitializeOverSharedConnectionAsync(global::Renamer.Renamer ext, SqliteConnection conn)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options;
            return new CoveContext(options, principalAccessor: null);
        });
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        services.AddSingleton<IAuthorizationService>(new RecordingAuthorizationService());
        var provider = services.BuildServiceProvider();
        await ext.InitializeAsync(provider);
    }

    // The caller the enqueue would have snapshotted, holding exactly the given permissions.
    private static CovePrincipal Caller(params string[] permissions)
        => FakePrincipalAccessor.WithPermissions(permissions).Current!;

    private const string RunId = "scan-under-test";

    // The run the enqueue would have minted, carrying the owner key the host mints for this caller.
    // The readback matches on both, so a test that stores under one caller and reads as another reads
    // 404.
    private static ScanRun RunFor(CovePrincipal? caller) => new(RunId, JobOwner.FromPrincipal(caller)?.Key);

    private static int StatusOf(IResult result) => Assert.IsType<IStatusCodeHttpResult>(Unwrap(result), exactMatch: false).StatusCode ?? 0;

    [Fact]
    public async Task ScanLibraryEnqueue_WithAnyReadPermission_Returns202_AndEnqueuesExclusiveOnce()
    {
        var (ext, _) = await NewExtensionAsync();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

        var result = ext.ScanLibraryEnqueue(null, principal, jobs);

        Assert.Equal(202, StatusOf(result));
        var (type, _, exclusive) = Assert.Single(jobs.Enqueued);
        Assert.Equal("ext:com.alextomas955.renamer:scan-library", type);
        Assert.True(exclusive);
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
            var caller = Caller(Permissions.VideosRead, Permissions.ImagesRead, Permissions.AudiosRead);
            await ext.RunScanLibraryJobAsync(
                caller, [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio], null,
                RunFor(caller), progress, default);

            var json = await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey);
            Assert.False(string.IsNullOrEmpty(json));

            var summary = JsonSerializer.Deserialize<global::Renamer.Contracts.ScanSummary>(json!, global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions)!;

            // Per kind, not flat - that split is what lets the readback drop a kind the caller cannot see.
            Assert.Equal(
                [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio],
                summary.Kinds.Select(k => k.Kind).ToHashSet());
            Assert.Equal(4, summary.Kinds.Sum(k => k.Files));
            Assert.Equal(4, summary.Kinds.Sum(k => k.Entities));
            Assert.All(summary.Kinds, k => Assert.Equal(k.Files, k.StatusCounts.Sum(c => c.Count)));

            var principal = FakePrincipalAccessor.WithPermissions(
                Permissions.VideosRead, Permissions.ImagesRead, Permissions.AudiosRead);
            var page = await ReadRowsAsync(ext, principal);
            Assert.Equal(4, page.Rows.Count);
            Assert.Contains(videoFileId1, page.Rows.Select(r => r.FileId));
            Assert.Contains(videoFileId2, page.Rows.Select(r => r.FileId));
            Assert.Equal(
                [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio],
                page.Rows.Select(r => r.Kind).ToHashSet());
            Assert.Null(page.Next);

            var (afterVideoName, afterVideoPath) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId1);
            Assert.Equal(beforeVideoName, afterVideoName);
            Assert.Equal(beforeVideoPath, afterVideoPath);

            // The scan reports progress as it plans rather than jumping to 1.0 at the end.
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
            // Without images.read, only Video is in the captured readable set.
            var caller = Caller(Permissions.VideosRead);
            await ext.RunScanLibraryJobAsync(
                caller, [RenamerFileKind.Video], null, RunFor(caller), progress, default);

            var json = await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey);
            var summary = JsonSerializer.Deserialize<global::Renamer.Contracts.ScanSummary>(json!, global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions)!;

            var kind = Assert.Single(summary.Kinds);
            Assert.Equal(RenamerFileKind.Video, kind.Kind);
            Assert.Equal(1, kind.Files);

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
    public async Task ScanLibraryEnqueue_WithAnOptionsBody_RunsAJobThatScansThoseOptions_NotTheSavedOnes()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await ExecutorTestSeed.SeedVideoAsync(db, dir.Root.Replace('\\', '/'), "raw.mkv", "One");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "video-bytes");

            var (ext, _) = await NewExtensionAsync();
            await InitializeOverSharedConnectionAsync(ext, conn);
            var jobs = new RecordingJobService();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            // The saved "$title" renames raw.mkv to One.mkv. The posted template renders the name the
            // file already has, so only a job that received the body counts it as unchanged.
            var body = new global::Renamer.Api.ScanLibraryRequest(
                JsonSerializer.Serialize(new RenamerOptions { FilenameTemplate = "raw" }, RenamerOptions.JsonOptions));
            var accepted = ext.ScanLibraryEnqueue(body, principal, jobs);
            Assert.Equal(202, StatusOf(accepted));
            await jobs.RunTheOnlyJobAsync();

            var summary = await ReadSummaryAsync(ext, principal, EnqueuedRunId(accepted));
            Assert.Equal(1, summary.TotalFiles);
            Assert.Equal(1, summary.NoChange);
            Assert.Equal(0, summary.WillChange);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunScanLibraryJobAsync_WithOverrideOptions_UsesThemOverSavedOptions()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The scan previews the source on disk, so the seeded row needs a real on-disk file - a
            // gone source would be SkipMissingSource, not the previewed rename this test asserts.
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, _, videoFileId) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "one.mkv", "One");
            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "video-bytes");

            // The saved template is "$title". The override below carries a literal prefix, so a scan
            // that honors it produces a different new name than a scan of the saved options would.
            var (ext, _) = await NewExtensionAsync();
            await InitializeOverSharedConnectionAsync(ext, conn);

            var overrideOptions = new RenamerOptions { FilenameTemplate = "DRYRUN - $title" };

            var progress = new FakeJobProgress();
            var caller = Caller(Permissions.VideosRead);
            await ext.RunScanLibraryJobAsync(
                caller, [RenamerFileKind.Video], overrideOptions, RunFor(caller), progress, default);

            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var summary = await ReadSummaryAsync(ext, principal);
            Assert.Equal(1, summary.WillChange);

            // The page plans with the options the caller sends, so passing the same override reproduces
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

    [Fact]
    public async Task ScanRowsAsync_KindTurnedOff_ServesNoRowsForIt()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "one.mkv", "One");
            await ExecutorTestSeed.SeedImageAsync(db, "/library/pics", "pic.jpg", "Pic");

            var (ext, _) = await NewExtensionAsync();
            await InitializeOverSharedConnectionAsync(ext, conn);

            var options = new RenamerOptions
            {
                Kinds = { [RenamerFileKind.Video] = new KindOptions { Enabled = false } },
            };

            var principal = FakePrincipalAccessor.WithPermissions(
                Permissions.VideosRead, Permissions.ImagesRead);
            var page = await ReadRowsAsync(ext, principal, new global::Renamer.Contracts.ScanRowsRequest(
                Options: JsonSerializer.Serialize(options, RenamerOptions.JsonOptions),
                Kind: null, AfterEntityId: null, Take: null, Query: null, Bucket: null));

            // The summary counts a kind that is off as unscanned, so a table that still lists its items
            // as gated skips contradicts the figures printed beside it, and reaching those rows spends
            // the request's entity budget on a kind nobody asked about.
            Assert.DoesNotContain(RenamerFileKind.Video, page.Rows.Select(r => r.Kind));
            Assert.Equal([RenamerFileKind.Image], page.Rows.Select(r => r.Kind).Distinct());
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

        var result = await ext.ScanLibraryResultAsync(RunId, principal, default);

        Assert.IsType<NotFound>(Unwrap(result));
    }

    private static async Task<global::Renamer.Contracts.ScanSummaryView> ReadSummaryAsync(
        global::Renamer.Renamer ext, ICurrentPrincipalAccessor principal, string runId = RunId)
    {
        var result = await ext.ScanLibraryResultAsync(runId, principal, default);
        return Assert.IsType<Ok<global::Renamer.Contracts.ScanSummaryView>>(Unwrap(result)).Value!;
    }

    // The run id the enqueue minted, which is the only handle its caller has on the figures.
    private static string EnqueuedRunId(IResult accepted) =>
        Assert.IsType<global::Renamer.Contracts.ScanEnqueued>(
            Assert.IsType<IValueHttpResult>(Unwrap(accepted), exactMatch: false).Value).RunId;

    private static async Task<global::Renamer.Contracts.ScanRowsPage> ReadRowsAsync(
        global::Renamer.Renamer ext, ICurrentPrincipalAccessor principal,
        global::Renamer.Contracts.ScanRowsRequest? body = null)
    {
        var result = await ext.ScanRowsAsync(body, principal, default);
        return Assert.IsType<Ok<global::Renamer.Contracts.ScanRowsPage>>(Unwrap(result)).Value!;
    }

    // A one-kind aggregate whose per-status counts are the only thing the readback merges.
    private static global::Renamer.Contracts.ScanKindSummary MakeKind(
        RenamerFileKind kind, int files, RenamerStatus status) =>
        new(kind, Entities: files, Files: files,
            StatusCounts: [.. Enum.GetValues<RenamerStatus>()
                .Select(s => new global::Renamer.Contracts.ScanStatusCount(s, s == status ? files : 0))],
            BlastRadius: new PreviewSummary(
                files, files, 0, 0, [], ConfirmLevel.Light, InFlightPathOverflowCount: 0),
            VolumePairsTruncated: false);

    // Stores an aggregate as the run these tests read back, owned by the principal they read it as.
    private static Task StoreSummaryAsync(
        FakeStore store, params global::Renamer.Contracts.ScanKindSummary[] kinds)
        => StoreSummaryAsAsync(store, RunFor(Caller()), kinds);

    private static Task StoreSummaryAsAsync(
        FakeStore store, ScanRun run, params global::Renamer.Contracts.ScanKindSummary[] kinds)
        => store.SetAsync(
            global::Renamer.Renamer.LastScanSummaryKey,
            JsonSerializer.Serialize(
                new global::Renamer.Contracts.ScanSummary(
                    global::Renamer.Contracts.ScanSummary.CurrentSchemaVersion, run.RunId, run.OwnerKey,
                    42L, kinds),
                global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions));

    [Fact]
    public async Task ScanLibraryResultAsync_VideoOnlyCaller_ReturnsOnlyVideoFigures_NotImageOrAudio()
    {
        var (ext, store) = await NewExtensionAsync();
        await StoreSummaryAsync(store,
            MakeKind(RenamerFileKind.Video, 3, RenamerStatus.Rename),
            MakeKind(RenamerFileKind.Image, 5, RenamerStatus.Rename),
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
            MakeKind(RenamerFileKind.Video, 3, RenamerStatus.Rename),
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
    public async Task ScanLibraryResultAsync_AScanThatCompletedAfterTheCallers_Reads404()
    {
        var (ext, store) = await NewExtensionAsync();

        await StoreSummaryAsAsync(
            store, new ScanRun("a-later-scan", JobOwner.FromPrincipal(Caller())?.Key),
            MakeKind(RenamerFileKind.Video, 9, RenamerStatus.Rename));

        Assert.IsType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(
            RunId, FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), default)));
    }

    [Fact]
    public async Task ScanLibraryResultAsync_AScanRunByAnotherPrincipal_Reads404()
    {
        var (ext, store) = await NewExtensionAsync();

        await StoreSummaryAsAsync(
            store, new ScanRun(RunId, "user:999"),
            MakeKind(RenamerFileKind.Video, 9, RenamerStatus.Rename));

        Assert.IsType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(
            RunId, FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), default)));
    }

    [Fact]
    public async Task ScanLibraryEnqueue_ReturnsARunIdPerEnqueue()
    {
        var (ext, _) = await NewExtensionAsync();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

        var first = EnqueuedRunId(ext.ScanLibraryEnqueue(null, principal, new RecordingJobService()));
        var second = EnqueuedRunId(ext.ScanLibraryEnqueue(null, principal, new RecordingJobService()));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task ScanLibraryResultAsync_UnparseableOrUnknownSchema_Reads404_NotA500()
    {
        var (ext, store) = await NewExtensionAsync();

        await store.SetAsync(global::Renamer.Renamer.LastScanSummaryKey, "{not json");
        Assert.IsType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(
            RunId, FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), default)));

        await store.SetAsync(
            global::Renamer.Renamer.LastScanSummaryKey,
            JsonSerializer.Serialize(
                new global::Renamer.Contracts.ScanSummary(
                    global::Renamer.Contracts.ScanSummary.CurrentSchemaVersion + 1, RunId,
                    JobOwner.FromPrincipal(Caller())?.Key, 0L, []),
                global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions));
        Assert.IsType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(
            RunId, FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), default)));
    }

    [Fact]
    public async Task ScanLibraryResultAsync_ASummaryStoredWithTheOldRenamerStatusName_Reads404()
    {
        var (ext, store) = await NewExtensionAsync();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
        var run = RunFor(Caller());
        string current = JsonSerializer.Serialize(
            new global::Renamer.Contracts.ScanSummary(
                global::Renamer.Contracts.ScanSummary.CurrentSchemaVersion, run.RunId, run.OwnerKey, 42L,
                [MakeKind(RenamerFileKind.Video, 3, RenamerStatus.Rename)]),
            global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions);
        await store.SetAsync(global::Renamer.Renamer.LastScanSummaryKey, current);
        Assert.IsNotType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(RunId, principal, default)));

        Assert.Contains("\"rename\"", current);
        await store.SetAsync(global::Renamer.Renamer.LastScanSummaryKey, current.Replace("\"rename\"", "\"renamer\""));

        Assert.IsType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(RunId, principal, default)));
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
            var caller = Caller(Permissions.VideosRead);
            await ext.RunScanLibraryJobAsync(
                caller, [RenamerFileKind.Video], null, RunFor(caller), progress, default);

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
}
