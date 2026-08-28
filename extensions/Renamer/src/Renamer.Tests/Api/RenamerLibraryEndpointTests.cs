using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// The whole-library renamer: <c>RenamerLibraryEnqueue</c> gates on ANY renamer-write permission and
/// enqueues, and <c>RunRenamerLibraryJobAsync</c> calls the EXISTING <c>RunRenamerBatchAsync</c> once per
/// kind that has at least one candidate id — never a synthetic combined kind. Exercised as plain
/// methods (no HTTP host) with a real SQLite <c>CoveContext</c> and real on-disk files, mirroring
/// <c>RenamerBatchJobTests</c>/<c>EntityIdsCapTests</c>.
/// </summary>
public sealed class RenamerLibraryEndpointTests
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

    private static async Task<(global::Renamer.Renamer ext, FakeStore store)> NewExtensionAsync(SqliteConnection conn)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options;
            return new CoveContext(options, principalAccessor: null);
        });
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        var provider = services.BuildServiceProvider();

        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        // Pin a stable title-only template so seeded (height-less) rows render a deterministic name,
        // independent of the shipped default template.
        await new OptionsStore(store).SaveAsync(new RenamerOptions { FilenameTemplate = "$title" });
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);
        return (ext, store);
    }

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

    /// <summary>
    /// Seeds one video and one image — each in its own folder, since <c>Folder.Path</c> is
    /// unique-indexed — with real bytes on disk, and returns the two file ids.
    /// </summary>
    private static async Task<(int VideoFileId, int ImageFileId)> SeedVideoAndImageAsync(CoveContext db, TempDir dir)
    {
        string videoFolder = Path.Combine(dir.Root, "videos").Replace('\\', '/');
        string imageFolder = Path.Combine(dir.Root, "images").Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(dir.Root, "videos"));
        Directory.CreateDirectory(Path.Combine(dir.Root, "images"));
        var (_, _, videoFileId) = await ExecutorTestSeed.SeedVideoAsync(db, videoFolder, "raw.mkv", "Film");
        var (_, _, imageFileId) = await ExecutorTestSeed.SeedImageAsync(db, imageFolder, "raw.jpg", "Pic");
        File.WriteAllText(Path.Combine(dir.Root, "videos", "raw.mkv"), "video-bytes");
        File.WriteAllText(Path.Combine(dir.Root, "images", "raw.jpg"), "image-bytes");
        return (videoFileId, imageFileId);
    }

    [Fact]
    public async Task RenamerLibraryEnqueue_WithAnyWritePermission_Returns202_AndEnqueuesExclusiveOnce()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, _) = await NewExtensionAsync(conn);
            var jobs = new RecordingJobService();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);

            var result = ext.RenamerLibraryEnqueue(principal, jobs);

            Assert.Equal(202, StatusOf(result));
            Assert.Single(jobs.Enqueued);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task RenamerLibraryEnqueue_WithNoWritePermission_Returns403_AndDoesNotEnqueue()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, _) = await NewExtensionAsync(conn);
            var jobs = new RecordingJobService();
            var principal = FakePrincipalAccessor.None();

            var result = ext.RenamerLibraryEnqueue(principal, jobs);

            Assert.Equal(403, StatusOf(result));
            Assert.Empty(jobs.Enqueued);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunRenamerLibraryJobAsync_VideoAndImageCandidates_OpensOneBatchPerKind_NeverACombinedBatch()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (videoFileId, imageFileId) = await SeedVideoAndImageAsync(db, dir);

            var (ext, store) = await NewExtensionAsync(conn);
            var progress = new FakeJobProgress();

            await ext.RunRenamerLibraryJobAsync([RenamerFileKind.Video, RenamerFileKind.Image], progress, default);

            // Both kinds actually renamed on disk.
            Assert.True(File.Exists(Path.Combine(dir.Root, "videos", "Film.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "images", "Pic.jpg")));

            var (videoBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId);
            var (imageBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, imageFileId);
            Assert.Equal("Film.mkv", videoBasename);
            Assert.Equal("Pic.jpg", imageBasename);

            // One batch PER KIND, never one combined batch across kinds: two batch rows, each naming
            // one kind and holding that kind's file alone. A combined batch would instead be a single
            // row carrying BOTH files.
            var batches = await db.Set<RevertBatchEntity>().AsNoTracking()
                .OrderBy(b => b.Kind).ToListAsync();
            Assert.Equal(
                [nameof(RenamerFileKind.Image), nameof(RenamerFileKind.Video)],
                batches.Select(b => b.Kind));
            Assert.All(batches, b => Assert.Equal(1, b.OriginalCount));

            var imageBatch = batches.Single(b => b.Kind == nameof(RenamerFileKind.Image));
            using var journal = new CoveRevertJournal(db);
            var imageRow = Assert.Single(
                await journal.ReadBatchPageAsync(imageBatch.RunId, long.MaxValue, 10));
            Assert.Equal(imageFileId, imageRow.FileId);

            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A run spanning two kinds reports a bar that only ever advances, and lands on 1.0 once.
    /// </summary>
    /// <remarks>
    /// Each per-kind batch scales its own [0,1] bar and reports 1.0 when it ends, so a kind handed the
    /// caller's sink verbatim restarts the bar below where the previous kind left it. Asserted over the
    /// recorded SEQUENCE, because a final-value check passes on exactly that behavior.
    /// </remarks>
    [Fact]
    public async Task RunRenamerLibraryJobAsync_TwoKinds_ReportsAdvancingProgress_AndReaches1Once()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedVideoAndImageAsync(db, dir);

            var (ext, _) = await NewExtensionAsync(conn);
            var progress = new FakeJobProgress();

            await ext.RunRenamerLibraryJobAsync([RenamerFileKind.Video, RenamerFileKind.Image], progress, default);

            var percents = progress.Reports.Select(r => r.Percent).ToList();
            Assert.NotEmpty(percents);
            for (int i = 1; i < percents.Count; i++)
            {
                Assert.True(
                    percents[i] >= percents[i - 1],
                    $"progress went backward at report {i}: [{string.Join(", ", percents)}]");
            }

            Assert.Equal(1d, percents[^1]);
            Assert.Equal(1, percents.Count(p => p == 1d));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunRenamerLibraryJobAsync_KindWithZeroCandidates_OpensNoBatchForThatKind()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "video-bytes");
            // No image/audio rows seeded at all.

            var (ext, store) = await NewExtensionAsync(conn);
            var progress = new FakeJobProgress();

            // Caller only holds videos.write + images.write (no audios.write) and there ARE zero
            // image candidates in the DB — both the permission filter and the empty-candidate skip
            // land on a kind that opens no batch.
            await ext.RunRenamerLibraryJobAsync([RenamerFileKind.Video, RenamerFileKind.Image], progress, default);

            // Only Video opened a batch — Image had zero candidates, so RunRenamerBatchAsync was never
            // called for it and no empty batch opened.
            var batch = Assert.Single(await db.Set<RevertBatchEntity>().AsNoTracking().ToListAsync());
            Assert.Equal(nameof(RenamerFileKind.Video), batch.Kind);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunRenamerLibraryJobAsync_MissingImagesWrite_LeavesImageRowUntouched_ButRenamesVideo()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (videoFileId, imageFileId) = await SeedVideoAndImageAsync(db, dir);

            var (beforeImageName, beforeImagePath) = await ExecutorTestSeed.ReadFileAsync(db, imageFileId);

            var (ext, _) = await NewExtensionAsync(conn);
            var progress = new FakeJobProgress();

            // Caller's captured writable set holds only Video (images.write was missing at enqueue time).
            await ext.RunRenamerLibraryJobAsync([RenamerFileKind.Video], progress, default);

            // Video renamed.
            var (videoBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId);
            Assert.Equal("Film.mkv", videoBasename);
            Assert.True(File.Exists(Path.Combine(dir.Root, "videos", "Film.mkv")));

            // Image untouched on disk and in the DB — the kind was never in the writable set, so the
            // job loop never even queried its candidates.
            Assert.True(File.Exists(Path.Combine(dir.Root, "images", "raw.jpg")));
            var (afterImageName, afterImagePath) = await ExecutorTestSeed.ReadFileAsync(db, imageFileId);
            Assert.Equal(beforeImageName, afterImageName);
            Assert.Equal(beforeImagePath, afterImagePath);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
