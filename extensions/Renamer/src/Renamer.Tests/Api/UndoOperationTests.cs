using Cove.Core.Auth;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// One whole-library rename is one undoable action: every kind it renamed comes back on a single
/// <c>/undo</c>, the panel's summary describes the whole of it, and a caller missing one kind's write
/// permission undoes none of it.
/// </summary>
public sealed class UndoOperationTests
{
    private static async Task<global::Renamer.Renamer> NewExtensionAsync(
        SqliteConnection conn, RenamerOptions? options = null, params string[] libraryPaths)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var contextOptions = new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options;
            return new CoveContext(contextOptions, principalAccessor: null);
        });
        services.AddSingleton<IEventBus>(new CapturingEventBus());
        services.AddLibraryPaths(libraryPaths);
        var provider = services.BuildServiceProvider();

        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(
            options ?? new RenamerOptions { FilenameTemplate = "$title" });
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);
        return ext;
    }

    private static int StatusOf(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

    private static UndoResult UndoValue(IResult result) =>
        Assert.IsType<UndoResult>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    private static LastBatchSummary LastBatchValue(IResult result) =>
        Assert.IsType<LastBatchSummary>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    /// <summary>Seeds one video and one image, each in its own folder with real bytes on disk.</summary>
    private static async Task<(int VideoFileId, int ImageFileId)> SeedVideoAndImageAsync(
        DbContext db, TempDir dir)
    {
        Directory.CreateDirectory(Path.Combine(dir.Root, "videos"));
        Directory.CreateDirectory(Path.Combine(dir.Root, "images"));
        string videoFolder = Path.Combine(dir.Root, "videos").Replace('\\', '/');
        string imageFolder = Path.Combine(dir.Root, "images").Replace('\\', '/');

        var (_, _, videoFileId) = await ExecutorTestSeed.SeedVideoAsync(db, videoFolder, "raw.mkv", "Film");
        var (_, _, imageFileId) = await ExecutorTestSeed.SeedImageAsync(db, imageFolder, "raw.jpg", "Pic");
        File.WriteAllText(Path.Combine(dir.Root, "videos", "raw.mkv"), "video-bytes");
        File.WriteAllText(Path.Combine(dir.Root, "images", "raw.jpg"), "image-bytes");
        return (videoFileId, imageFileId);
    }

    private static FakePrincipalAccessor WritesBoth =>
        FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite, Permissions.ImagesWrite);

    [Fact]
    public async Task AWholeLibraryRun_WritesOneOperationIdAcrossEveryKindsBatch()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedVideoAndImageAsync(db, dir);
            var ext = await NewExtensionAsync(conn);

            await ext.RunRenamerLibraryJobAsync(
                [RenamerFileKind.Video, RenamerFileKind.Image], new FakeJobProgress(), default);

            var batches = await db.Set<RevertBatchEntity>().AsNoTracking().ToListAsync();
            Assert.Equal(2, batches.Count);
            var operationId = Assert.Single(batches.Select(b => b.OperationId).Distinct());
            Assert.NotEqual("", operationId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheSummary_IsTheOperationsTotals_AndItsEarliestTimestamp()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedVideoAndImageAsync(db, dir);
            var ext = await NewExtensionAsync(conn);

            await ext.RunRenamerLibraryJobAsync(
                [RenamerFileKind.Video, RenamerFileKind.Image], new FakeJobProgress(), default);

            var read = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var summary = LastBatchValue(await ext.LastBatchAsync(read, default));

            // Both kinds' files, summed — not the last kind's batch alone.
            Assert.True(summary.HasBatch);
            Assert.Equal(2, summary.Count);
            Assert.Equal(2, summary.RemainingCount);
            Assert.False(summary.Consumed);

            var opened = await db.Set<RevertBatchEntity>().AsNoTracking()
                .Select(b => b.OpenedAtUtcTicks).ToListAsync();
            Assert.Equal(opened.Min(), summary.WrittenAtUtcTicks);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task OneUndo_RestoresEveryKindTheRunRenamed()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (videoFileId, imageFileId) = await SeedVideoAndImageAsync(db, dir);
            var ext = await NewExtensionAsync(conn);

            await ext.RunRenamerLibraryJobAsync(
                [RenamerFileKind.Video, RenamerFileKind.Image], new FakeJobProgress(), default);
            Assert.True(File.Exists(Path.Combine(dir.Root, "videos", "Film.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "images", "Pic.jpg")));

            var undo = UndoValue(await ext.UndoAsync(WritesBoth, default));

            Assert.Equal(2, undo.Undone);
            Assert.True(File.Exists(Path.Combine(dir.Root, "videos", "raw.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "images", "raw.jpg")));

            var (videoBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId);
            var (imageBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, imageFileId);
            Assert.Equal("raw.mkv", videoBasename);
            Assert.Equal("raw.jpg", imageBasename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task AKindThatCannotComeBackYet_LeavesItsRowsForARetry_AndTheUndoStillTerminates()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, imageFileId) = await SeedVideoAndImageAsync(db, dir);
            string imageFolder = Path.Combine(dir.Root, "images").Replace('\\', '/');
            string destFolder = Path.Combine(dir.Root, "dest").Replace('\\', '/');
            Directory.CreateDirectory(Path.Combine(dir.Root, "dest"));

            // The image is routed off its own folder, so that folder can be taken away without taking
            // the renamed file with it.
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                PathDestinations = [new PathDestinationRule { Pattern = imageFolder, Dest = Dest.At(destFolder) }],
            };
            var ext = await NewExtensionAsync(
                conn, options, Path.Combine(dir.Root, "videos").Replace('\\', '/'), imageFolder, destFolder);

            await ext.RunRenamerLibraryJobAsync(
                [RenamerFileKind.Video, RenamerFileKind.Image], new FakeJobProgress(), default);
            Assert.True(File.Exists(Path.Combine(dir.Root, "videos", "Film.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "dest", "Pic.jpg")));

            // A missing original directory is never recreated, so the image rows stop for a reason the
            // world can clear and stay in the journal.
            Directory.Delete(Path.Combine(dir.Root, "images"), recursive: true);

            var partial = UndoValue(await ext.UndoAsync(WritesBoth, default));
            Assert.Equal(1, partial.Undone);
            Assert.Equal(1, partial.SkippedCount);
            Assert.True(File.Exists(Path.Combine(dir.Root, "videos", "raw.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "dest", "Pic.jpg")));

            // The batch whose rows all stopped still holds them, so the batch cursor — not their
            // absence — is what ends the loop. A second undo over the same state terminates and acts
            // only on what is still outstanding.
            var again = UndoValue(await ext.UndoAsync(WritesBoth, default));
            Assert.Equal(0, again.Undone);
            Assert.Equal(1, again.SkippedCount);

            Directory.CreateDirectory(Path.Combine(dir.Root, "images"));
            var retry = UndoValue(await ext.UndoAsync(WritesBoth, default));
            Assert.Equal(1, retry.Undone);
            Assert.Equal(0, retry.SkippedCount);

            var (imageBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, imageFileId);
            Assert.Equal("raw.jpg", imageBasename);
            Assert.True(File.Exists(Path.Combine(dir.Root, "images", "raw.jpg")));

            var read = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            Assert.True(LastBatchValue(await ext.LastBatchAsync(read, default)).Consumed);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ACallerMissingOneOfTheOperationsKinds_Undoes403_AndRestoresNothing()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedVideoAndImageAsync(db, dir);
            var ext = await NewExtensionAsync(conn);

            await ext.RunRenamerLibraryJobAsync(
                [RenamerFileKind.Video, RenamerFileKind.Image], new FakeJobProgress(), default);

            var videosOnly = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
            Assert.Equal(403, StatusOf(await ext.UndoAsync(videosOnly, default)));

            // Refusing the whole operation is the claim, so the video half must be untouched too.
            Assert.True(File.Exists(Path.Combine(dir.Root, "videos", "Film.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "images", "Pic.jpg")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
