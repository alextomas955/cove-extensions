using System.Text.Json;
using Cove.Core.Auth;
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

    // Wires the extension's captured seams (_scopeFactory, _eventBus) from a DI provider whose
    // DbContext registration is scoped over conn, so the job body's own CreateAsyncScope() resolves
    // a context over the same database the test seeded - mirrors
    // RenamerBatchJobTests.BuildExtensionAsync. The scan job never touches IEventBus, but
    // InitializeAsync requires both seams to be resolvable.
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
            await ext.RunScanLibraryJobAsync(
                Caller(Permissions.VideosRead, Permissions.ImagesRead, Permissions.AudiosRead),
                [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio], null, progress, default);

            var json = await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey);
            Assert.False(string.IsNullOrEmpty(json));

            var summary = JsonSerializer.Deserialize<global::Renamer.Contracts.ScanSummary>(json!, global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions)!;

            // Per kind, not flat - that split is what lets the readback drop a kind the caller cannot see.
            Assert.Equal(
                [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio],
                summary.Kinds.Select(k => k.Kind).ToHashSet());
            Assert.Equal(4, summary.Kinds.Sum(k => k.Files));
            Assert.Equal(4, summary.Kinds.Sum(k => k.Entities));
            // Exact, not sampled: every kind's per-status counts account for all of its files.
            Assert.All(summary.Kinds, k => Assert.Equal(k.Files, k.StatusCounts.Sum(c => c.Count)));

            // The rows themselves come from the page query, planned on demand - every seeded file appears.
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

            // Zero mutation: the seeded row is byte-for-byte unchanged after the scan job ran.
            var (afterVideoName, afterVideoPath) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId1);
            Assert.Equal(beforeVideoName, afterVideoName);
            Assert.Equal(beforeVideoPath, afterVideoPath);

            // Progress feedback: the scan must report intermediate progress as it plans, not jump
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
            // Caller holds videos.read but not images.read - only Video is in the captured readable set.
            await ext.RunScanLibraryJobAsync(
                Caller(Permissions.VideosRead), [RenamerFileKind.Video], null, progress, default);

            var json = await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey);
            var summary = JsonSerializer.Deserialize<global::Renamer.Contracts.ScanSummary>(json!, global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions)!;

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
            Assert.Equal(202, StatusOf(ext.ScanLibraryEnqueue(body, principal, jobs)));
            await jobs.RunTheOnlyJobAsync();

            var summary = await ReadSummaryAsync(ext, principal);
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

            // Saved options template is "$title" (from NewExtensionAsync). The override below uses a
            // different template with a literal prefix, so a scan that honors the override produces a
            // visibly different new name than a scan of the saved options would.
            var (ext, _) = await NewExtensionAsync();
            await InitializeOverSharedConnectionAsync(ext, conn);

            var overrideOptions = new RenamerOptions { FilenameTemplate = "DRYRUN - $title" };

            var progress = new FakeJobProgress();
            await ext.RunScanLibraryJobAsync(
                Caller(Permissions.VideosRead), [RenamerFileKind.Video], overrideOptions, progress, default);

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

        var result = await ext.ScanLibraryResultAsync(principal, default);

        Assert.IsType<NotFound>(Unwrap(result));
    }

    // Invokes the readback and unwraps the merged view.
    private static async Task<global::Renamer.Contracts.ScanSummaryView> ReadSummaryAsync(
        global::Renamer.Renamer ext, ICurrentPrincipalAccessor principal)
    {
        var result = await ext.ScanLibraryResultAsync(principal, default);
        return Assert.IsType<Ok<global::Renamer.Contracts.ScanSummaryView>>(Unwrap(result)).Value!;
    }

    // Invokes the page query and unwraps the page.
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

    private static Task StoreSummaryAsync(FakeStore store, params global::Renamer.Contracts.ScanKindSummary[] kinds)
        => store.SetAsync(
            global::Renamer.Renamer.LastScanSummaryKey,
            JsonSerializer.Serialize(
                new global::Renamer.Contracts.ScanSummary(
                    global::Renamer.Contracts.ScanSummary.CurrentSchemaVersion, 42L, kinds),
                global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions));

    [Fact]
    public async Task ScanLibraryResultAsync_VideoOnlyCaller_ReturnsOnlyVideoFigures_NotImageOrAudio()
    {
        // A higher-permission user's scan persisted Video+Image+Audio figures under the fixed key. A
        // video-only caller reading it back must not receive the image/audio counts (the cross-kind leak).
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
                global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions));
        Assert.IsType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), default)));
    }

    [Fact]
    public async Task ScanLibraryResultAsync_ASummaryStoredWithTheOldRenamerStatusName_Reads404()
    {
        var (ext, store) = await NewExtensionAsync();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
        string current = JsonSerializer.Serialize(
            new global::Renamer.Contracts.ScanSummary(
                global::Renamer.Contracts.ScanSummary.CurrentSchemaVersion, 42L,
                [MakeKind(RenamerFileKind.Video, 3, RenamerStatus.Rename)]),
            global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions);
        await store.SetAsync(global::Renamer.Renamer.LastScanSummaryKey, current);
        Assert.IsNotType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(principal, default)));

        Assert.Contains("\"rename\"", current);
        await store.SetAsync(global::Renamer.Renamer.LastScanSummaryKey, current.Replace("\"rename\"", "\"renamer\""));

        Assert.IsType<NotFound>(Unwrap(await ext.ScanLibraryResultAsync(principal, default)));
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
            await ext.RunScanLibraryJobAsync(
                Caller(Permissions.VideosRead), [RenamerFileKind.Video], null, progress, default);

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
