using Cove.Core.Auth;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

[Collection(SubstDriveScope.CollectionName)]
public sealed class RenamerLibraryEndpointTests
{
    private static async Task<(global::Renamer.Renamer ext, FakeStore store)> NewExtensionAsync(
        SqliteConnection conn, RenamerOptions? renamerOptions = null, string[]? libraryPaths = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options;
            return new CoveContext(options, principalAccessor: null);
        });
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        services.AddSingleton<IAuthorizationService>(new RecordingAuthorizationService());
        if (libraryPaths is not null)
        {
            services.AddLibraryPaths(libraryPaths);
        }

        var provider = services.BuildServiceProvider();

        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        // Pin a stable title-only template so seeded (height-less) rows render a deterministic name,
        // independent of the shipped default template.
        await new OptionsStore(store).SaveAsync(
            renamerOptions ?? new RenamerOptions { FilenameTemplate = "$title" });
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);
        return (ext, store);
    }

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

    // The caller the enqueue would have snapshotted, holding exactly the given permissions.
    private static CovePrincipal Caller(params string[] permissions)
        => FakePrincipalAccessor.WithPermissions(permissions).Current!;

    // Seeds one video and one image - each in its own folder, since Folder.Path is unique-indexed -
    // with real bytes on disk, and returns the two file ids.
    private static async Task<(int VideoFileId, int ImageFileId)> SeedVideoAndImageAsync(DbContext db, TempDir dir)
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
    public async Task RunRenamerLibraryJobAsync_VideoAndImageCandidates_OpensOneBatchPerKind_NeverACombinedBatch()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (videoFileId, imageFileId) = await SeedVideoAndImageAsync(db, dir);

            var (ext, _) = await NewExtensionAsync(conn);
            var progress = new FakeJobProgress();

            await ext.RunRenamerLibraryJobAsync(
                Caller(Permissions.VideosWrite, Permissions.ImagesWrite),
                [RenamerFileKind.Video, RenamerFileKind.Image], progress, default);

            // Both kinds actually renamed on disk.
            Assert.True(File.Exists(Path.Combine(dir.Root, "videos", "Film.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "images", "Pic.jpg")));

            var (videoBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId);
            var (imageBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, imageFileId);
            Assert.Equal("Film.mkv", videoBasename);
            Assert.Equal("Pic.jpg", imageBasename);

            // One batch per kind, never one combined batch across kinds: two batch rows, each naming
            // one kind and holding that kind's file alone. A combined batch would instead be a single
            // row carrying both files.
            var batches = await db.Set<RevertBatchEntity>().AsNoTracking()
                .OrderBy(b => b.Kind).ToListAsync();
            Assert.Equal(
                [nameof(RenamerFileKind.Image), nameof(RenamerFileKind.Video)],
                batches.Select(b => b.Kind));
            Assert.All(batches, b => Assert.Equal(1, b.OriginalCount));

            var imageBatch = batches.Single(b => b.Kind == nameof(RenamerFileKind.Image));
            await using var journal = new CoveRevertJournal(db);
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

            await ext.RunRenamerLibraryJobAsync(
                Caller(Permissions.VideosWrite, Permissions.ImagesWrite),
                [RenamerFileKind.Video, RenamerFileKind.Image], progress, default);

            var percents = progress.Reports.Select(r => r.Percent).ToList();
            Assert.NotEmpty(percents);
            for (int i = 1; i < percents.Count; i++)
            {
                Assert.True(
                    percents[i] >= percents[i - 1],
                    $"progress went backward at report {i}: [{string.Join(", ", percents)}]");
            }

            Assert.Equal(1d, percents[^1]);

            // Compared within a tolerance rather than exactly: the slice drops anything that reaches
            // the run's end, so the only report that can land here is the run's own final one.
            Assert.Equal(1, percents.Count(p => Math.Abs(p - 1d) < 1e-9));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunRenamerLibraryJobAsync_FreeSpaceRefusal_FinalReportNamesIt_NotComplete()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var dir = new TempDir();
        using var destination = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedVideoAndImageAsync(db, dir);

            string destRoot = destination.Root.Replace('\\', '/').TrimEnd('/');
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                Kinds =
                {
                    [RenamerFileKind.Video] = new KindOptions { Destination = new Destination { Root = destRoot } },
                },
            };

            var (ext, _) = await NewExtensionAsync(
                conn, options, libraryPaths: [dir.Root.Replace('\\', '/').TrimEnd('/'), destRoot]);
            var progress = new FakeJobProgress();

            // The destination volume reports no room, so the video move is refused before any copy.
            await ext.RunRenamerLibraryJobAsync(
                Caller(Permissions.VideosWrite, Permissions.ImagesWrite),
                [RenamerFileKind.Video, RenamerFileKind.Image], progress, default, _ => 0L);

            string final = progress.Reports[^1].Message ?? string.Empty;
            Assert.DoesNotContain("Library rename complete.", final, StringComparison.Ordinal);
            Assert.Contains("insufficient free space", final, StringComparison.Ordinal);
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

            // Caller only holds videos.write + images.write (no audios.write) and there are zero
            // image candidates in the DB - both the permission filter and the empty-candidate skip
            // land on a kind that opens no batch.
            await ext.RunRenamerLibraryJobAsync(
                Caller(Permissions.VideosWrite, Permissions.ImagesWrite),
                [RenamerFileKind.Video, RenamerFileKind.Image], progress, default);

            // Only Video opened a batch - Image had zero candidates, so RunRenamerBatchAsync was never
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
            await ext.RunRenamerLibraryJobAsync(
                Caller(Permissions.VideosWrite), [RenamerFileKind.Video], progress, default);

            // Video renamed.
            var (videoBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId);
            Assert.Equal("Film.mkv", videoBasename);
            Assert.True(File.Exists(Path.Combine(dir.Root, "videos", "Film.mkv")));

            // Image untouched on disk and in the DB - the kind was never in the writable set, so the
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
