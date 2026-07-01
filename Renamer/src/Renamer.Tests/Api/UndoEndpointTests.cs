using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Api;
using Renamer.Jobs;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// The <c>/undo</c> + <c>/last-batch</c> API surface, driven end-to-end on the real spine
/// (SQLite + a real <see cref="TempDir"/>, mirroring <see cref="RenamerExecutorIntegrationTests"/>).
/// Each test first performs a REAL renamer through <c>RunRenamerBatchAsync</c> (so a genuine one-batch
/// log is written to the extension's store) and then exercises the endpoints on the SAME extension
/// instance — the RevertLog blob lives in the extension's <see cref="FakeStore"/>, the undo event is
/// captured on the wired <see cref="CapturingEventBus"/>, and the DbContext is resolved from the
/// wired scope factory exactly as the production handler does. Proves: round-trip restore (disk + DB
/// + correct entity event), header-driven kind (an image batch publishes ImageUpdated — never a Video
/// default), consume-on-undo (second undo + empty-log are no-ops), and the summary read shape.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class UndoEndpointTests
{
    /// <summary>
    /// Wires the extension's captured seams from a DI provider that registers the seeded context as
    /// the base <c>DbContext</c> (singleton, so the scope resolves the same seeded instance) and the
    /// given capturing event bus, plus a fresh <see cref="FakeStore"/> for the RevertLog. Mirrors
    /// <c>RenamerBatchJobTests.BuildExtensionAsync</c>.
    /// </summary>
    private static async Task<(global::Renamer.Renamer ext, FakeStore store)> BuildExtensionAsync(CoveContext db, IEventBus bus)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        services.AddSingleton(bus);
        var provider = services.BuildServiceProvider();

        var store = new FakeStore();
        var ext = new global::Renamer.Renamer();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider); // captures IServiceScopeFactory + IEventBus from DI
        return (ext, store);
    }

    /// <summary>Seeds the extension's stored options so a renamer renamers to "$title".</summary>
    private static Task SeedTitleOptionsAsync(FakeStore store) =>
        new global::Renamer.Options.OptionsStore(store)
            .SaveAsync(new global::Renamer.Options.RenamerOptions { FilenameTemplate = "$title" });

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static UndoResult UndoValue(IResult result) =>
        Assert.IsType<UndoResult>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static LastBatchSummary LastBatchValue(IResult result) =>
        Assert.IsType<LastBatchSummary>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    [Fact]
    public async Task Undo_RoundTrip_RestoresDiskAndDb_PublishesEntityEvent_AndConsumesBatch()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Offset the Video id sequence so videoId != fileId — the published undo event must carry
            // the ENTITY id from the log row, never the file id.
            db.Set<Video>().Add(new Video { Title = "decoy", Organized = true });
            await db.SaveChangesAsync();
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");
            Assert.NotEqual(videoId, fileId);

            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var bus = new CapturingEventBus();
            var (ext, store) = await BuildExtensionAsync(db, bus);
            await SeedTitleOptionsAsync(store); // → "My Film.mkv"

            // Forward renamer via the shared batch core — writes one real batch to the store.
            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), new FakeJobProgress(), default);
            Assert.True(File.Exists(newFull));
            Assert.False(File.Exists(oldFull));
            bus.Published.Clear(); // drop the forward event; we assert only the undo event below.

            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
            var result = await ext.UndoAsync(principal, default);

            Assert.Equal(200, StatusOf(result));
            var undo = UndoValue(result);
            Assert.Equal(1, undo.Undone);
            Assert.Empty(undo.Failed);
            Assert.Empty(undo.Skipped);

            // Disk restored.
            Assert.True(File.Exists(oldFull), "file restored to OLD");
            Assert.False(File.Exists(newFull), "NEW gone after undo");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));

            // DB restored.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw clip.mkv", basename);
            Assert.Equal(folderPath + "/raw clip.mkv", path);

            // Event: exactly one VideoUpdated for the PARENT entity id (≠ fileId).
            var evt = Assert.IsType<EntityEvent>(Assert.Single(bus.Published));
            Assert.Equal(EventType.VideoUpdated, evt.Type);
            Assert.Equal("Video", evt.EntityType);
            Assert.Equal(videoId, evt.EntityId);
            Assert.NotEqual(fileId, evt.EntityId);

            // Batch consumed: a SECOND undo is a no-op.
            var second = await ext.UndoAsync(principal, default);
            var secondUndo = UndoValue(second);
            Assert.Equal(0, secondUndo.Undone);
            Assert.Empty(secondUndo.Failed);
            Assert.Empty(secondUndo.Skipped);
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
            var (ext, store) = await BuildExtensionAsync(db, bus);
            await SeedTitleOptionsAsync(store);

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("image", [imageId]), new FakeJobProgress(), default);
            Assert.True(File.Exists(newFull));
            bus.Published.Clear();

            // Undoing an IMAGE batch requires images.write (the batch header carries the kind) — not
            // videos.write. This proves the per-kind permission gate on the undo path.
            var result = await ext.UndoAsync(FakePrincipalAccessor.WithPermissions(Permissions.ImagesWrite), default);
            Assert.Equal(1, UndoValue(result).Undone);

            // The published event is ImageUpdated — proving the kind comes from the batch HEADER,
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
    public async Task Undo_RestoresNothing_LeavesBatchOpen_SoCorrectedRetrySucceeds()
    {
        // A run that restores NOTHING (every entry skipped) must NOT consume the batch: the undo is
        // the only recovery path, and consuming it on an all-skipped run would strand the file at its
        // new location forever. Here the restore target is rejected by an allowlist that does not yet
        // cover the original folder; after the allowlist is corrected, a retry must still recover.
        using var srcDir = new TempDir();
        using var destDir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcPath = srcDir.Root.Replace('\\', '/');
            string destPath = destDir.Root.Replace('\\', '/');
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, srcPath, "raw clip.mkv", "My Film");

            string oldFull = Path.Combine(srcDir.Root, "raw clip.mkv");
            string newFull = Path.Combine(destDir.Root, "My Film.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var (ext, store) = await BuildExtensionAsync(db, new CapturingEventBus());
            // Forward: a routed move OFF the source folder onto the dest folder (a relocation, so the
            // undo re-gate applies). Both roots allowed for the forward move.
            await new global::Renamer.Options.OptionsStore(store).SaveAsync(new global::Renamer.Options.RenamerOptions
            {
                FilenameTemplate = "$title",
                AllowedRoots = [srcPath, destPath],
                PathDestinations = [new global::Renamer.Options.PathDestinationRule { Pattern = srcPath, Dest = destPath }],
            });
            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), new FakeJobProgress(), default);
            Assert.True(File.Exists(newFull), "forward move landed on dest");
            Assert.False(File.Exists(oldFull));

            var write = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
            var read = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            // Undo with an allowlist that NO LONGER covers the original source folder → every entry is
            // skipped (restore target rejected by allowlist), undone == 0.
            await new global::Renamer.Options.OptionsStore(store).SaveAsync(new global::Renamer.Options.RenamerOptions
            {
                FilenameTemplate = "$title",
                AllowedRoots = [destPath],   // source root deliberately omitted
            });
            var skippedRun = UndoValue(await ext.UndoAsync(write, default));
            Assert.Equal(0, skippedRun.Undone);
            Assert.Single(skippedRun.Skipped);
            Assert.True(File.Exists(newFull), "file still on dest — nothing restored");

            // The batch MUST remain open (not consumed) so it can be retried.
            var afterSkip = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.True(afterSkip.HasBatch);
            Assert.False(afterSkip.Consumed, "an all-skipped undo must NOT consume the batch");

            // Correct the allowlist to cover the original location, then retry: the recovery succeeds.
            await new global::Renamer.Options.OptionsStore(store).SaveAsync(new global::Renamer.Options.RenamerOptions
            {
                FilenameTemplate = "$title",
                AllowedRoots = [srcPath, destPath],
            });
            var retryRun = UndoValue(await ext.UndoAsync(write, default));
            Assert.Equal(1, retryRun.Undone);
            Assert.Empty(retryRun.Skipped);
            Assert.True(File.Exists(oldFull), "file restored to original after corrected retry");
            Assert.False(File.Exists(newFull));
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));

            // Now — and only now — the batch is consumed.
            var afterRetry = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.True(afterRetry.Consumed, "batch consumed once a retry actually restored an entry");
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
            var (ext, _) = await BuildExtensionAsync(db, new CapturingEventBus());

            var result = await ext.UndoAsync(FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite), default);

            Assert.Equal(200, StatusOf(result));
            var undo = UndoValue(result);
            Assert.Equal(0, undo.Undone);
            Assert.Empty(undo.Failed);
            Assert.Empty(undo.Skipped);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LastBatch_AfterRenamer_ReportsSummary_ThenFalseOnEmptyLog()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var (ext, store) = await BuildExtensionAsync(db, new CapturingEventBus());
            await SeedTitleOptionsAsync(store);

            var read = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            // Before any renamer: no batch.
            var empty = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.False(empty.HasBatch);
            Assert.Equal(0, empty.Count);

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), new FakeJobProgress(), default);

            // After a renamer: a one-row, not-yet-consumed batch with a real server timestamp.
            var summary = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.True(summary.HasBatch);
            Assert.Equal(1, summary.Count);
            Assert.False(summary.Consumed);
            Assert.True(summary.WrittenAtUtcTicks > 0);

            // After an undo: the batch is consumed.
            await ext.UndoAsync(FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite), default);
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

    /// <summary>Seeds an Image + one ImageFile in the given (already-seeded or new) folder. Returns (imageId, fileId).</summary>
    private static async Task<(int imageId, int fileId)> SeedImageAsync(
        CoveContext db, string folderPath, string basename, string title)
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
