using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

// The /undo and /last-batch handlers on a real SQLite database and a real temp directory. Each test
// performs a real rename through RunRenamerBatchAsync first, so the journal table holds a genuine batch.
public sealed class UndoEndpointTests
{
    // Wires the extension's captured seams from a DI provider that registers the seeded context as
    // the base DbContext (singleton, so the scope resolves the same seeded instance) and the given
    // capturing event bus, plus a fresh FakeStore for the options.
    private static readonly RenamerOptions TitleOptions = new() { FilenameTemplate = "$title" };

    private static int StatusOf(IResult result) => Assert.IsType<IStatusCodeHttpResult>(Unwrap(result), exactMatch: false).StatusCode ?? 0;

    private static UndoResult UndoValue(IResult result) =>
        Assert.IsType<UndoResult>(Assert.IsType<IValueHttpResult>(Unwrap(result), exactMatch: false).Value);

    private static LastBatchSummary LastBatchValue(IResult result) =>
        Assert.IsType<LastBatchSummary>(Assert.IsType<IValueHttpResult>(Unwrap(result), exactMatch: false).Value);

    [Fact]
    public async Task Undo_RoundTrip_RestoresDiskAndDb_PublishesEntityEvent_AndConsumesBatch()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Offset the Video id sequence so videoId != fileId - the published undo event must carry
            // the entity id from the log row, never the file id.
            db.Set<Video>().Add(new Video { Title = "decoy", Organized = true });
            await db.SaveChangesAsync();
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");
            Assert.NotEqual(videoId, fileId);

            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var bus = new CapturingEventBus();
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, TitleOptions, bus);

            // Forward renamer via the shared batch core - writes one real batch to the journal.
            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId], new FakeJobProgress(), default);
            Assert.True(File.Exists(newFull));
            Assert.False(File.Exists(oldFull));
            bus.Published.Clear(); // drop the forward event; we assert only the undo event below.

            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
            var result = await ext.UndoAsync(principal, new RecordingAuthorizationService(), default);

            Assert.Equal(200, StatusOf(result));
            var undo = UndoValue(result);
            Assert.Equal(1, undo.Undone);
            Assert.Empty(undo.FailedSample);
            Assert.Equal(0, undo.FailedCount);
            Assert.Empty(undo.SkippedSample);
            Assert.Equal(0, undo.SkippedCount);

            // Disk restored.
            Assert.True(File.Exists(oldFull), "file restored to OLD");
            Assert.False(File.Exists(newFull), "NEW gone after undo");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));

            // DB restored.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw clip.mkv", basename);
            Assert.Equal(folderPath + "/raw clip.mkv", path);

            // Event: exactly one VideoUpdated for the parent entity id (≠ fileId).
            var evt = Assert.IsType<EntityEvent>(Assert.Single(bus.Published));
            Assert.Equal(EventType.VideoUpdated, evt.Type);
            Assert.Equal("Video", evt.EntityType);
            Assert.Equal(videoId, evt.EntityId);
            Assert.NotEqual(fileId, evt.EntityId);

            // Batch consumed: a second undo is a no-op.
            var second = await ext.UndoAsync(principal, new RecordingAuthorizationService(), default);
            var secondUndo = UndoValue(second);
            Assert.Equal(0, secondUndo.Undone);
            Assert.Empty(secondUndo.FailedSample);
            Assert.Equal(0, secondUndo.FailedCount);
            Assert.Empty(secondUndo.SkippedSample);
            Assert.Equal(0, secondUndo.SkippedCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Undo_ImageBatch_PublishesImageUpdated_KindFromHeader_NoVideoDefault()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Offset the Image id sequence so imageId != fileId.
            db.Set<Image>().Add(new Image { Title = "decoy", Organized = true });
            await db.SaveChangesAsync();
            var (imageId, fileId) = await SeedImageAsync(db, folderPath, "raw shot.jpg", "My Photo");
            Assert.NotEqual(imageId, fileId);

            string oldFull = Path.Combine(dir.Root, "raw shot.jpg");
            string newFull = Path.Combine(dir.Root, "My Photo.jpg");
            File.WriteAllText(oldFull, "image-bytes");

            var bus = new CapturingEventBus();
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, TitleOptions, bus);

            await ext.RunRenamerBatchAsync(RenamerFileKind.Image, [imageId], new FakeJobProgress(), default);
            Assert.True(File.Exists(newFull));
            bus.Published.Clear();

            // Undoing an image batch requires images.write (the batch header carries the kind) - not
            // videos.write. This proves the per-kind permission gate on the undo path.
            var result = await ext.UndoAsync(
                FakePrincipalAccessor.WithPermissions(Permissions.ImagesWrite),
                new RecordingAuthorizationService(), default);
            Assert.Equal(1, UndoValue(result).Undone);

            // The published event is ImageUpdated - proving the kind comes from the batch header,
            // never a hardcoded RenamerFileKind.Video default on the undo path.
            var evt = Assert.IsType<EntityEvent>(Assert.Single(bus.Published));
            Assert.Equal(EventType.ImageUpdated, evt.Type);
            Assert.Equal("Image", evt.EntityType);
            Assert.Equal(imageId, evt.EntityId);

            Assert.True(File.Exists(oldFull), "image restored to OLD");
            Assert.False(File.Exists(newFull), "NEW gone after undo");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Undo_EmptyLog_IsCleanNoOp()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, new RenamerOptions());

            var result = await ext.UndoAsync(
                FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite),
                new RecordingAuthorizationService(), default);

            Assert.Equal(200, StatusOf(result));
            var undo = UndoValue(result);
            Assert.Equal(0, undo.Undone);
            Assert.Empty(undo.FailedSample);
            Assert.Equal(0, undo.FailedCount);
            Assert.Empty(undo.SkippedSample);
            Assert.Equal(0, undo.SkippedCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Undo_BatchOlderThanTheRetentionWindow_RestoresNothing_AndTheFileStaysRenamed()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");

            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, TitleOptions, new CapturingEventBus());

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId], new FakeJobProgress(), default);
            Assert.True(File.Exists(newFull));

            // The state a library nobody renames for longer than the window is in. Back-dating the row
            // is the arrangement rather than a clock the handler reads, because the handler reads
            // DateTime.UtcNow and the batch's own timestamp is the only other side of that comparison.
            var batch = await db.Set<RevertBatchEntity>().SingleAsync();
            batch.OpenedAtUtcTicks =
                (DateTime.UtcNow - CoveRevertJournal.RetentionWindow - TimeSpan.FromMinutes(1)).Ticks;
            await db.SaveChangesAsync();

            var result = await ext.UndoAsync(
                FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite),
                new RecordingAuthorizationService(), default);

            Assert.Equal(200, StatusOf(result));
            var undo = UndoValue(result);
            Assert.Equal(0, undo.Undone);

            // The claim is about the disk and the DB, not only about the reported count: a restore that
            // ran and then reported nothing would satisfy the count alone.
            Assert.True(File.Exists(newFull), "the expired batch was replayed and moved the file back");
            Assert.False(File.Exists(oldFull));
            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LastBatch_IsEmptyFirst_ThenReportsTheRename_ThenIsConsumedOnceUndone()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, TitleOptions, new CapturingEventBus());

            var read = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            // Before any renamer: no batch.
            var empty = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.False(empty.HasBatch);
            Assert.Equal(0, empty.Count);

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId], new FakeJobProgress(), default);

            // After a renamer: a one-row, not-yet-consumed batch with a real server timestamp.
            var summary = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.True(summary.HasBatch);
            Assert.Equal(1, summary.Count);
            Assert.False(summary.Consumed);
            Assert.True(summary.WrittenAtUtcTicks > 0);

            // After an undo: the batch is consumed.
            await ext.UndoAsync(
                FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite),
                new RecordingAuthorizationService(), default);
            var consumed = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.True(consumed.HasBatch);
            Assert.True(consumed.Consumed);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Undo_OfATextBatch_IsAllowedForACallerHoldingOnlyTextsWrite()
    {
        // The coarse gate that runs before the journal is read admits a caller holding any renamer
        // write permission. It has to be read off the same array every other path reads, because a
        // caller whose only kind is text holds none of the other three: a second copy of that list
        // refuses them here while the rename that made the batch was allowed.
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, textId, _) = await ExecutorTestSeed.SeedTextAsync(
                db, folderPath, "raw scan.pdf", "A Manual");

            string oldFull = Path.Combine(dir.Root, "raw scan.pdf");
            string newFull = Path.Combine(dir.Root, "A Manual.pdf");
            File.WriteAllText(oldFull, "text-bytes");

            var bus = new CapturingEventBus();
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, TitleOptions, bus);

            await ext.RunRenamerBatchAsync(RenamerFileKind.Text, [textId], new FakeJobProgress(), default);
            Assert.True(File.Exists(newFull));

            var textsOnly = FakePrincipalAccessor.WithPermissions(Permissions.TextsWrite);
            var result = await ext.UndoAsync(textsOnly, new RecordingAuthorizationService(), default);

            Assert.Equal(1, UndoValue(result).Undone);
            Assert.True(File.Exists(oldFull), "file restored to old path");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // Seeds an Image + one ImageFile in the given (already-seeded or new) folder. Returns (imageId,
    // fileId).
    private static async Task<(int imageId, int fileId)> SeedImageAsync(
        DbContext db, string folderPath, string basename, string title)
    {
        var folder = new Folder { Path = folderPath.Replace('\\', '/'), ModTime = DateTime.UtcNow };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync();

        var image = new Image { Title = title, Organized = true };
        db.Set<Image>().Add(image);
        await db.SaveChangesAsync();

        var file = new ImageFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            Format = basename.Contains('.') ? basename[(basename.LastIndexOf('.') + 1)..] : "",
            ImageId = image.Id,
        };
        db.Set<ImageFile>().Add(file);
        await db.SaveChangesAsync();
        return (image.Id, file.Id);
    }
}
