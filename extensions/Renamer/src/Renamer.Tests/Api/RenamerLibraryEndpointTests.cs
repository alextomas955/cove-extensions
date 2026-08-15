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
/// <c>RenamerExecutorIntegrationTests</c>' batch cases and <c>EntityIdsCapTests</c>.
/// </summary>
[Trait("Tier", "L1")]
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

    // Unwrapped first: a handler declaring Results<…> hands back a union that carries no status of its
    // own and converts implicitly to IResult, so an un-unwrapped read throws instead of reporting one.
    private static int StatusOf(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

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
            // Folder.Path is unique-indexed, so video and image need distinct folder rows.
            string videoFolder = Path.Combine(dir.Root, "videos").Replace('\\', '/');
            string imageFolder = Path.Combine(dir.Root, "images").Replace('\\', '/');
            Directory.CreateDirectory(Path.Combine(dir.Root, "videos"));
            Directory.CreateDirectory(Path.Combine(dir.Root, "images"));
            var (_, _, videoFileId) = await ExecutorTestSeed.SeedVideoAsync(db, videoFolder, "raw.mkv", "Film");
            var (_, _, imageFileId) = await ExecutorTestSeed.SeedImageAsync(db, imageFolder, "raw.jpg", "Pic");
            File.WriteAllText(Path.Combine(dir.Root, "videos", "raw.mkv"), "video-bytes");
            File.WriteAllText(Path.Combine(dir.Root, "images", "raw.jpg"), "image-bytes");

            var (ext, _) = await NewExtensionAsync(conn);
            var progress = new FakeJobProgress();

            await ext.RunRenamerLibraryJobAsync([RenamerFileKind.Video, RenamerFileKind.Image], progress, default);

            // Both kinds actually renamed on disk.
            Assert.True(File.Exists(Path.Combine(dir.Root, "videos", "Film.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "images", "Pic.jpg")));

            var (videoBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId);
            var (imageBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, imageFileId);
            Assert.Equal("Film.mkv", videoBasename);
            Assert.Equal("Pic.jpg", imageBasename);

            // One batch PER KIND, never one combined batch across kinds. The newest batch is the second
            // kind's, carrying its kind alone and only that kind's row; a combined batch would carry BOTH
            // files. Retiring that row then surfaces the FIRST kind's batch, still intact — opening the
            // second batch never cost the first its rows.
            var journal = new CoveRevertJournal(db);

            var imageBatch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(imageBatch);
            Assert.Equal(RenamerFileKind.Image, imageBatch!.Kind);
            var imageRow = Assert.Single(imageBatch.Rows);
            Assert.Equal(imageFileId, imageRow.FileId);

            await journal.DeleteRowAsync(imageRow.RunId, imageRow.Seq, unrestorable: false);

            var videoBatch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(videoBatch);
            Assert.Equal(RenamerFileKind.Video, videoBatch!.Kind);
            Assert.Equal(videoFileId, Assert.Single(videoBatch.Rows).FileId);

            Assert.Equal(1d, progress.LastPercent);
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

            var (ext, _) = await NewExtensionAsync(conn);
            var progress = new FakeJobProgress();

            // Caller only holds videos.write + images.write (no audios.write) and there ARE zero
            // image candidates in the DB — both the permission filter and the empty-candidate skip
            // land on a kind that opens no batch.
            await ext.RunRenamerLibraryJobAsync([RenamerFileKind.Video, RenamerFileKind.Image], progress, default);

            // Only Video opened a batch — Image had zero candidates, so RunRenamerBatchAsync was never
            // called for it and no empty batch opened. Counted on the TABLE rather than inferred from
            // the undo target: that read names the newest batch with rows left, so an empty Image batch
            // opened after the Video one would be passed over silently and leave the assertion below
            // reading exactly as it does now.
            Assert.Equal(1, await db.Set<RevertBatchEntity>().AsNoTracking().CountAsync());

            var summary = await new CoveRevertJournal(db).ReadUndoTargetAsync();
            Assert.NotNull(summary);
            Assert.Equal(1, summary!.Value.OriginalCount);
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
            // Folder.Path is unique-indexed, so video and image need distinct folder rows.
            string videoFolder = Path.Combine(dir.Root, "videos").Replace('\\', '/');
            string imageFolder = Path.Combine(dir.Root, "images").Replace('\\', '/');
            Directory.CreateDirectory(Path.Combine(dir.Root, "videos"));
            Directory.CreateDirectory(Path.Combine(dir.Root, "images"));
            var (_, _, videoFileId) = await ExecutorTestSeed.SeedVideoAsync(db, videoFolder, "raw.mkv", "Film");
            var (_, _, imageFileId) = await ExecutorTestSeed.SeedImageAsync(db, imageFolder, "raw.jpg", "Pic");
            File.WriteAllText(Path.Combine(dir.Root, "videos", "raw.mkv"), "video-bytes");
            File.WriteAllText(Path.Combine(dir.Root, "images", "raw.jpg"), "image-bytes");

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
