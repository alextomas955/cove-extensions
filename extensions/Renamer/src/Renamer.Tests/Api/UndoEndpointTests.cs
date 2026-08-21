using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Renamer.Contracts;
using Renamer.Jobs;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// The <c>/undo</c> + <c>/last-batch</c> API surface, driven end-to-end on the real spine
/// (SQLite + a real <see cref="TempDir"/>, mirroring <see cref="RenamerExecutorIntegrationTests"/>).
/// Each test first performs one or more REAL renamers through <c>RunRenamerBatchAsync</c> (so genuine
/// batches are journalled) and then exercises the endpoints on the SAME extension
/// instance — the journal is two tables in the same <see cref="CoveContext"/> those renames wrote
/// through, so a batch and the rows it opened are read back from where production reads them; the
/// <see cref="FakeStore"/> here carries only the extension's stored options; the undo event is captured
/// on the wired <see cref="CapturingEventBus"/>; and the DbContext is resolved from the wired scope
/// factory exactly as the production handler does. Proves: round-trip restore (disk + DB
/// + correct entity event), header-driven kind (an image batch publishes ImageUpdated — never a Video
/// default), a batch reaching SPENT as its rows retire rather than being consumed on the first partial
/// success (a second undo and an empty journal are no-ops), the summary read shape, and that one
/// journal read names the batch BOTH the summary and the button speak about — including once a newer
/// batch has settled over an older one that still holds rows, which is the state that used to render
/// as "No rename to undo." over a live remainder; and that two FULLY pending batches are walked back
/// one press at a time, the newer kind first and then the one before it, each restored in full — the
/// state a whole-library run leaves behind, which no other case here reaches because every one of them
/// first partly undoes or settles a batch.
/// </summary>
[Trait("Tier", "L1")]
public sealed class UndoEndpointTests
{
    /// <summary>Seeds the extension's stored options so a renamer renames to "$title".</summary>
    private static Task SeedTitleOptionsAsync(FakeStore store) =>
        new global::Renamer.Options.OptionsStore(store)
            .SaveAsync(new global::Renamer.Options.RenamerOptions { FilenameTemplate = "$title" });

    // Unwrapped first: a handler declaring Results<…> hands back a union that carries neither the
    // status nor the value itself, and converts implicitly to IResult — so without this these helpers
    // throw at the assertion rather than at the call site that widened the signature.
    private static int StatusOf(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

    // Fully qualified: `Renamer.Tests.Execution` is imported above, so the production
    // `Renamer.Execution` namespace is not in scope here. Named once, so the cap the assertions below
    // compare against is the shipped constant rather than a number copied beside it.
    private const int SampleCap = global::Renamer.Execution.UndoRunAccumulator.MaxSampleEntries;

    private static UndoResult UndoValue(IResult result) =>
        Assert.IsType<UndoResult>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    private static LastBatchSummary LastBatchValue(IResult result) =>
        Assert.IsType<LastBatchSummary>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

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
            var (ext, store) = await ExtensionHarness.CreateWithSharedContextAsync(db, bus);
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
            Assert.Equal(0, undo.FailedCount);
            Assert.Equal(0, undo.SkippedCount);

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
            Assert.Equal(0, secondUndo.FailedCount);
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
            var (ext, store) = await ExtensionHarness.CreateWithSharedContextAsync(db, bus);
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
    public async Task Undo_WithMoreProblemsAndMoreRestoresThanTheSampleCap_StatesTheRealTotals_AndRetiresEveryRow()
    {
        // The response describes at most UndoRunAccumulator.MaxSampleEntries entries per channel. This
        // case is the one that proves the cap bounds the DESCRIPTION and never the work, on both sides
        // of it: more problems than the cap in one run, then more restores than the cap in one run.
        //
        // Driven through the real handler over a real batch rather than by folding an accumulator by
        // hand — a cap wrongly reached into the retirement loop or the page loop is invisible to a fold
        // and visible only here, as rows left in the table.
        int blockedCount = SampleCap + 1;
        int total = blockedCount + 4;

        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, firstId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw 000.mkv", "Title 000");
            File.WriteAllText(Path.Combine(dir.Root, "raw 000.mkv"), "bytes 000");

            var entityIds = new List<int> { firstId };
            var originals = new List<string> { Path.Combine(dir.Root, "raw 000.mkv") };
            for (int i = 1; i < total; i++)
            {
                string basename = $"raw {i:000}.mkv";
                var (videoId, _) = await SeedVideoIntoFolderAsync(db, folderId, basename, $"Title {i:000}");
                File.WriteAllText(Path.Combine(dir.Root, basename), $"bytes {i:000}");
                entityIds.Add(videoId);
                originals.Add(Path.Combine(dir.Root, basename));
            }

            var (ext, store) = await ExtensionHarness.CreateWithSharedContextAsync(db, new CapturingEventBus());
            // One worker: every scope in this fixture resolves the one seeded context.
            await new global::Renamer.Options.OptionsStore(store).SaveAsync(new global::Renamer.Options.RenamerOptions
            {
                FilenameTemplate = "$title",
                SameVolumeConcurrency = 1,
            });

            var write = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
            var read = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            await ext.RunRenamerBatchAsync(
                RenamerJob.Encode("video", entityIds), new FakeJobProgress(), default);
            foreach (var original in originals)
            {
                Assert.False(File.Exists(original), $"forward rename left {original} in place");
            }

            // Occupy one more original slot than the sample can describe, so the first undo's skipped
            // TOTAL exceeds its skipped SAMPLE.
            foreach (var original in originals.Take(blockedCount))
            {
                File.WriteAllText(original, "someone else's file");
            }

            var partial = UndoValue(await ext.UndoAsync(write, default));

            Assert.Equal(total - blockedCount, partial.Undone);
            Assert.Equal(blockedCount, partial.SkippedCount);
            Assert.Equal(0, partial.FailedCount);
            // The number stated is the real one; the description is capped. A response that reported the
            // sample's length would say MaxSampleEntries here and be short by exactly one file.
            Assert.Equal(SampleCap, partial.SkippedSample.Count);
            Assert.True(
                partial.SkippedCount > partial.SkippedSample.Count,
                "the fixture must produce more problems than the sample describes, or it proves nothing");

            // Clear every obstruction and retry: one run now restores more files than the cap.
            foreach (var original in originals.Take(blockedCount))
            {
                File.Delete(original);
            }

            var retry = UndoValue(await ext.UndoAsync(write, default));

            Assert.Equal(blockedCount, retry.Undone);
            Assert.True(retry.Undone > SampleCap,
                "the retry must restore more files than the cap, or the cap-is-not-a-ceiling claim is untested");
            Assert.Equal(0, retry.SkippedCount);
            Assert.Equal(0, retry.FailedCount);

            // Every file is back on disk…
            foreach (var original in originals)
            {
                Assert.True(File.Exists(original), $"{original} did not come back");
            }

            // …every row was retired, and the batch aggregate agrees. This is the assertion a sample cap
            // that had leaked into the work would fail: rows left behind, or a restored count short of
            // the batch's original count.
            using var journal = new global::Renamer.Execution.CoveRevertJournal(db);
            Assert.Null(await JournalPageReader.ReadWholeUndoTargetAsync(journal));

            var spent = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.Equal(total, spent.Count);
            Assert.Equal(0, spent.RemainingCount);
            Assert.Equal(0, spent.UnrestorableCount);
            Assert.True(spent.Consumed);
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
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(db, srcPath, "raw clip.mkv", "My Film");

            string oldFull = Path.Combine(srcDir.Root, "raw clip.mkv");
            string newFull = Path.Combine(destDir.Root, "My Film.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var (ext, store) = await ExtensionHarness.CreateWithSharedContextAsync(
                db, new CapturingEventBus(), options: null, destPath);
            // Forward: a routed move OFF the source folder onto the dest folder (a relocation, so the
            // undo re-gate applies). Both roots allowed for the forward move.
            await new global::Renamer.Options.OptionsStore(store).SaveAsync(new global::Renamer.Options.RenamerOptions
            {
                FilenameTemplate = "$title",
                AllowedRoots = [srcPath, destPath],
                PathDestinations =
                [
                    new global::Renamer.Options.PathDestinationRule
                    {
                        Pattern = srcPath, Dest = TestSupport.Dests.At(destPath),
                    },
                ],
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
            Assert.Equal(1, skippedRun.SkippedCount);
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
            Assert.Equal(0, retryRun.SkippedCount);
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
    public async Task Undo_Twice_TheSecondCallActsOnlyOnWhatTheFirstLeft_ThenTheBatchIsSpent()
    {
        // The handler's own behaviour, not the journal's arithmetic: that a partially-restored batch is
        // still offered, that a second call works through what is left of it, and that a third call on a
        // finished batch answers rather than errors. The counters behind all this are pinned a tier down
        // by UndoRetryTests; repeating them here would make a failure harder to attribute.
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, comesId, comesFileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw comes.mkv", "Comes Back");
            var (blockedId, blockedFileId) = await SeedVideoIntoFolderAsync(db, folderId, "raw blocked.mkv", "Blocked");

            string comesOld = Path.Combine(dir.Root, "raw comes.mkv");
            string blockedOld = Path.Combine(dir.Root, "raw blocked.mkv");
            string blockedNew = Path.Combine(dir.Root, "Blocked.mkv");
            File.WriteAllText(comesOld, "comes-bytes");
            File.WriteAllText(blockedOld, "blocked-bytes");

            var (ext, store) = await ExtensionHarness.CreateWithSharedContextAsync(db, new CapturingEventBus());
            // One worker: every scope in this fixture resolves the one seeded context, and the batch
            // path's default fan-out would have two workers query it at once.
            await new global::Renamer.Options.OptionsStore(store).SaveAsync(new global::Renamer.Options.RenamerOptions
            {
                FilenameTemplate = "$title",
                SameVolumeConcurrency = 1,
            });
            await ext.RunRenamerBatchAsync(
                RenamerJob.Encode("video", [comesId, blockedId]), new FakeJobProgress(), default);
            Assert.True(File.Exists(blockedNew));

            // A cause the world can clear: the blocked file's original slot is taken, so undo refuses to
            // clobber and that entry stops.
            File.WriteAllText(blockedOld, "someone else's file");

            var write = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
            var read = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            // UNTOUCHED: everything the batch journalled is still outstanding. This is the state the
            // panel's "N items renamed" line has always described, and the one the new remaining figure
            // has to agree with rather than replace.
            var untouched = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.Equal(2, untouched.Count);
            Assert.Equal(2, untouched.RemainingCount);
            Assert.Equal(0, untouched.UnrestorableCount);

            var first = UndoValue(await ext.UndoAsync(write, default));
            Assert.Equal(1, first.Undone);
            Assert.Equal(1, first.SkippedCount);
            Assert.Equal(0, first.FailedCount);

            // The table behind that response holds exactly the row that did not come back — a response
            // that read right while the journal disagreed would fail here.
            using (var journal = new global::Renamer.Execution.CoveRevertJournal(db))
            {
                var open = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
                Assert.NotNull(open);
                Assert.Equal(blockedFileId, Assert.Single(open.Rows).FileId);
            }

            // PARTIALLY RESTORED: the figures the panel's one line is built from. The original count is
            // deliberately unmoved — it is what the user did — while the remaining count is what the
            // button now acts on, and the two together are what let the panel state "1 of 2 restored"
            // without the reader doing the subtraction.
            var partial = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.False(partial.Consumed, "a batch with work left is still offered");
            Assert.Equal(2, partial.Count);
            Assert.Equal(1, partial.RemainingCount);
            Assert.Equal(0, partial.UnrestorableCount);

            File.Delete(blockedOld);
            var second = UndoValue(await ext.UndoAsync(write, default));

            // ONE, not two: the first call's row is gone, so the second sees only the remaining work.
            Assert.Equal(1, second.Undone);
            Assert.Equal(0, second.SkippedCount);
            Assert.True(File.Exists(blockedOld), "the blocked file came back on the retry");
            Assert.True(File.Exists(comesOld), "and the first call's file was left alone");
            Assert.NotEqual(comesFileId, blockedFileId);

            // FULLY RESTORED: nothing outstanding, and the original count still says what was renamed.
            var spent = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.True(spent.Consumed);
            Assert.Equal(0, spent.RemainingCount);
            Assert.Equal(2, spent.Count);

            // A call against a finished batch is the existing "nothing to undo" answer, not an error.
            var third = await ext.UndoAsync(write, default);
            Assert.Equal(200, StatusOf(third));
            var thirdUndo = UndoValue(third);
            Assert.Equal(0, thirdUndo.Undone);
            Assert.Equal(0, thirdUndo.FailedCount);
            Assert.Equal(0, thirdUndo.SkippedCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LastBatch_OnceANewerBatchSettles_StillNamesTheOlderBatchWithRowsLeft_AndUndoActsOnIt()
    {
        // The sequence that stranded a pending retry, reproduced through the endpoints rather than by
        // seeding two batches by hand: a hand-seeded pair would prove the read and not the situation, and
        // the point of this case is that the situation needs no failure of its own to arise. A background
        // metadata edit opens its own batch per edit, so a newer batch settling over an older one that
        // still holds rows is the ORDINARY state.
        //
        // The defect this pins: a summary that reads the newest batch whatever its remaining count, while
        // /undo acts on the newest batch that still HAS rows, makes the two name different batches as soon
        // as the newer one settles — so the summary reports nothing remaining and the panel renders "No
        // rename to undo." over an older batch whose rows are still live and still restorable. One read,
        // naming one batch, is what forecloses it.
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, comesId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw comes.mkv", "Comes Back");
            var (blockedId, blockedFileId) =
                await SeedVideoIntoFolderAsync(db, folderId, "raw blocked.mkv", "Blocked");
            var (laterId, _) = await SeedVideoIntoFolderAsync(db, folderId, "raw later.mkv", "Later");

            string comesOld = Path.Combine(dir.Root, "raw comes.mkv");
            string blockedOld = Path.Combine(dir.Root, "raw blocked.mkv");
            string blockedNew = Path.Combine(dir.Root, "Blocked.mkv");
            string laterOld = Path.Combine(dir.Root, "raw later.mkv");
            File.WriteAllText(comesOld, "comes-bytes");
            File.WriteAllText(blockedOld, "blocked-bytes");
            File.WriteAllText(laterOld, "later-bytes");

            var (ext, store) = await ExtensionHarness.CreateWithSharedContextAsync(db, new CapturingEventBus());
            // One worker: every scope in this fixture resolves the one seeded context, and the batch
            // path's default fan-out would have two workers query it at once.
            await new global::Renamer.Options.OptionsStore(store).SaveAsync(new global::Renamer.Options.RenamerOptions
            {
                FilenameTemplate = "$title",
                SameVolumeConcurrency = 1,
            });

            var write = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
            var read = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            using var journal = new global::Renamer.Execution.CoveRevertJournal(db);

            // ── The deliberate run: two files in one batch. ────────────────────────────────────────
            await ext.RunRenamerBatchAsync(
                RenamerJob.Encode("video", [comesId, blockedId]), new FakeJobProgress(), default);
            string runA = (await journal.ReadUndoTargetAsync())!.Value.RunId;

            // One batch, holding rows, with nothing newer: the case this change must leave exactly as it
            // was. The run id is read off the journal because the wire summary deliberately carries none.
            var only = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.True(only.HasBatch);
            Assert.False(only.Consumed);
            Assert.Equal(2, only.Count);
            Assert.Equal(2, only.RemainingCount);

            // A partial undo: one file's original slot is occupied, so the reverse move refuses to
            // clobber and that row stays in the table — the remainder 26-U3 exists to keep retryable.
            File.WriteAllText(blockedOld, "someone else's file");
            var partial = UndoValue(await ext.UndoAsync(write, default));
            Assert.Equal(1, partial.Undone);
            Assert.Equal(1, partial.SkippedCount);

            // ── The newer batch, opened and then fully undone. No failure anywhere in it. ──────────
            await ext.RunRenamerBatchAsync(
                RenamerJob.Encode("video", [laterId]), new FakeJobProgress(), default);
            string runB = (await journal.ReadUndoTargetAsync())!.Value.RunId;
            Assert.NotEqual(runA, runB);

            Assert.Equal(1, UndoValue(await ext.UndoAsync(write, default)).Undone);
            Assert.True(File.Exists(laterOld), "the newer batch was fully undone");

            // ── The divergence state. One read names the batch, so both endpoints name the OLDER one. ──
            // Asserted on the run id, not only on the counts: two batches' counts could agree by
            // coincidence, and it is the identity that proves the summary and the button agree.
            Assert.Equal(runA, (await journal.ReadUndoTargetAsync())!.Value.RunId);

            var summary = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.True(summary.HasBatch);
            Assert.False(summary.Consumed, "the older batch still has a row to restore");
            Assert.Equal(2, summary.Count);
            Assert.Equal(1, summary.RemainingCount);

            // ── And the button acts on the batch that was just described. ──────────────────────────
            File.Delete(blockedOld);
            var retry = UndoValue(await ext.UndoAsync(write, default));
            Assert.Equal(1, retry.Undone);
            Assert.Equal(0, retry.SkippedCount);

            // On disk AND in the database: a summary that named the right batch while the undo acted on
            // another would still fail here.
            Assert.True(File.Exists(blockedOld), "the older batch's outstanding file is back");
            Assert.False(File.Exists(blockedNew));
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, blockedFileId);
            Assert.Equal("raw blocked.mkv", basename);
            Assert.Equal(folderPath + "/raw blocked.mkv", path);

            // The retirement landed on the OLDER batch's counters, which is the other half of "the undo
            // restored from the run the summary named". Read off that batch's own row by run id, not
            // through the target read — that read answers "the batch to act on", which is now the newer
            // settled one again, so asking it here would only confirm itself.
            var settledA = await db.Set<global::Renamer.Execution.RevertBatchEntity>()
                .AsNoTracking().SingleAsync(b => b.RunId == runA);
            Assert.Equal(2, settledA.OriginalCount);
            Assert.Equal(2, settledA.RestoredCount);
            Assert.Equal(0, settledA.UnrestorableCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Undo_TwiceAcrossTwoFullyPendingBatches_WalksBackFromTheNewerToTheOlder_AndRestoresBoth()
    {
        // TWO batches, BOTH fully pending, undone one after the other. Every other multi-batch case in
        // this file first partly undoes a batch or settles one, so none of them reaches this state — and
        // it is the ordinary state a whole-library run leaves, because such a run opens one batch per
        // kind and the button reaches one batch per press.
        //
        // The opener is ONE RunRenamerLibraryJobAsync call, which is legal only because a whole-library
        // run opens one batch PER KIND rather than one combined batch; that is pinned a tier down by
        // RenamerLibraryEndpointTests.RunRenamerLibraryJobAsync_VideoAndImageCandidates_OpensOneBatchPerKind_NeverACombinedBatch.
        //
        // Asserted on IDENTITY and not only on counts, at every step: both batches hold one row here, so
        // a second press that acted on the wrong batch — or on nothing at all while still answering 200
        // with Undone 0 — would satisfy a case written on totals alone. The run ids come off the journal
        // because the wire summary deliberately carries none.
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Folder.Path is unique-indexed, so the video and the image need distinct folder rows.
            string videoFolder = Path.Combine(dir.Root, "videos").Replace('\\', '/');
            string imageFolder = Path.Combine(dir.Root, "images").Replace('\\', '/');
            Directory.CreateDirectory(Path.Combine(dir.Root, "videos"));
            Directory.CreateDirectory(Path.Combine(dir.Root, "images"));

            var (_, _, videoFileId) = await ExecutorTestSeed.SeedVideoAsync(db, videoFolder, "raw film.mkv", "Film");
            var (_, _, imageFileId) = await ExecutorTestSeed.SeedImageAsync(db, imageFolder, "raw shot.jpg", "Pic");

            string videoOld = Path.Combine(dir.Root, "videos", "raw film.mkv");
            string videoNew = Path.Combine(dir.Root, "videos", "Film.mkv");
            string imageOld = Path.Combine(dir.Root, "images", "raw shot.jpg");
            string imageNew = Path.Combine(dir.Root, "images", "Pic.jpg");
            File.WriteAllText(videoOld, "video-bytes");
            File.WriteAllText(imageOld, "image-bytes");

            var (ext, store) = await ExtensionHarness.CreateWithSharedContextAsync(db, new CapturingEventBus());
            // One worker: every scope in this fixture resolves the one seeded context.
            await new global::Renamer.Options.OptionsStore(store).SaveAsync(new global::Renamer.Options.RenamerOptions
            {
                FilenameTemplate = "$title",
                SameVolumeConcurrency = 1,
            });

            // BOTH kinds' writes. The undo re-gates on the kind of the batch it is about to act on, so a
            // principal holding one kind's write would 403 on the second half of the walk-back.
            var write = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite, Permissions.ImagesWrite);
            var read = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead, Permissions.ImagesRead);
            using var journal = new global::Renamer.Execution.CoveRevertJournal(db);

            // ── One call, two batches. Video is listed first, so the IMAGE batch is the newer one. ──
            await ext.RunRenamerLibraryJobAsync(
                [global::Renamer.Planner.RenamerFileKind.Video, global::Renamer.Planner.RenamerFileKind.Image],
                new FakeJobProgress(),
                default);

            // Both kinds really renamed, on disk and in the database — so nothing below can proceed on a
            // run that did nothing.
            Assert.True(File.Exists(videoNew), "the video was renamed");
            Assert.True(File.Exists(imageNew), "the image was renamed");
            var (renamedVideo, _) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId);
            var (renamedImage, _) = await ExecutorTestSeed.ReadFileAsync(db, imageFileId);
            Assert.Equal("Film.mkv", renamedVideo);
            Assert.Equal("Pic.jpg", renamedImage);

            // ── The state no existing case reaches: two batches, each holding all of its work. ──────
            var opened = await db.Set<global::Renamer.Execution.RevertBatchEntity>().AsNoTracking().ToListAsync();
            Assert.Equal(2, opened.Count);

            string newerRunId = (await journal.ReadUndoTargetAsync())!.Value.RunId;
            var newerBatch = opened.Single(b => b.RunId == newerRunId);
            var olderBatch = opened.Single(b => b.RunId != newerRunId);

            // The offered batch is the IMAGE one — the kind the run reached SECOND — read off the batch
            // row rather than assumed from the order the kinds were passed in. Were that order ever to
            // change, this fails loudly instead of quietly walking the other way.
            Assert.Equal(nameof(global::Renamer.Planner.RenamerFileKind.Image), newerBatch.Kind);
            Assert.Equal(nameof(global::Renamer.Planner.RenamerFileKind.Video), olderBatch.Kind);

            // Each journalled exactly one file and neither has settled any of it. The original counts are
            // what the two Undone assertions below are compared against, so a batch that journalled
            // nothing would make those assertions vacuous — this is where that is foreclosed.
            foreach (var batch in opened)
            {
                Assert.Equal(1, batch.OriginalCount);
                Assert.Equal(0, batch.RestoredCount);
                Assert.Equal(0, batch.UnrestorableCount);
            }

            var offered = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.True(offered.HasBatch);
            Assert.False(offered.Consumed, "both batches are still fully pending");
            Assert.Equal(newerBatch.OriginalCount, offered.RemainingCount);

            // ── First press: the newer kind comes back, and the older one is left exactly where it is. ──
            var first = UndoValue(await ext.UndoAsync(write, default));
            Assert.Equal(newerBatch.OriginalCount, first.Undone);
            Assert.Equal(0, first.SkippedCount);
            Assert.Equal(0, first.FailedCount);

            Assert.True(File.Exists(imageOld), "the newer kind is back at its original name");
            Assert.False(File.Exists(imageNew));
            var (imageBasename, imagePath) = await ExecutorTestSeed.ReadFileAsync(db, imageFileId);
            Assert.Equal("raw shot.jpg", imageBasename);
            Assert.Equal(imageFolder + "/raw shot.jpg", imagePath);

            Assert.True(File.Exists(videoNew), "one press reaches one kind — the older one is untouched");
            Assert.False(File.Exists(videoOld));

            // ── The walk-back itself. Asserted on the run id: the two batches' counts are equal here, so
            // only identity can tell an offer that moved from one that did not. ─────────────────────
            Assert.Equal(olderBatch.RunId, (await journal.ReadUndoTargetAsync())!.Value.RunId);

            // ── Second press: the kind before it comes back too. ────────────────────────────────────
            var second = UndoValue(await ext.UndoAsync(write, default));
            Assert.NotEqual(0, second.Undone);
            Assert.Equal(olderBatch.OriginalCount, second.Undone);
            Assert.Equal(0, second.SkippedCount);
            Assert.Equal(0, second.FailedCount);

            Assert.True(File.Exists(videoOld), "the older kind is back at its original name");
            Assert.False(File.Exists(videoNew));
            var (videoBasename, videoPath) = await ExecutorTestSeed.ReadFileAsync(db, videoFileId);
            Assert.Equal("raw film.mkv", videoBasename);
            Assert.Equal(videoFolder + "/raw film.mkv", videoPath);

            // Both batches ended fully restored, read off their OWN rows by run id rather than through
            // the target read — that read answers "the batch to act on", so asking it here would only
            // confirm itself.
            foreach (var expected in opened)
            {
                var settled = await db.Set<global::Renamer.Execution.RevertBatchEntity>()
                    .AsNoTracking().SingleAsync(b => b.RunId == expected.RunId);
                Assert.Equal(expected.OriginalCount, settled.RestoredCount);
                Assert.Equal(0, settled.UnrestorableCount);
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LastBatch_WithEveryBatchSettled_StillDescribesTheNewestOne_AndAFurtherUndoIsANoOp()
    {
        // The fallback arm of the one selection read. Nothing is replayable, so there is no batch to
        // prefer — and the newest aggregate is still the right answer, because a settled rename is
        // exactly what the panel has to be able to describe ("1 file renamed, 1 restored").
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, firstId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw first.mkv", "First");
            var (secondId, _) = await SeedVideoIntoFolderAsync(db, folderId, "raw second.mkv", "Second");

            File.WriteAllText(Path.Combine(dir.Root, "raw first.mkv"), "first-bytes");
            File.WriteAllText(Path.Combine(dir.Root, "raw second.mkv"), "second-bytes");

            var (ext, store) = await ExtensionHarness.CreateWithSharedContextAsync(db, new CapturingEventBus());
            await SeedTitleOptionsAsync(store);

            var write = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
            var read = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [firstId]), new FakeJobProgress(), default);
            Assert.Equal(1, UndoValue(await ext.UndoAsync(write, default)).Undone);

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [secondId]), new FakeJobProgress(), default);
            Assert.Equal(1, UndoValue(await ext.UndoAsync(write, default)).Undone);

            var summary = LastBatchValue(await ext.LastBatchAsync(read, default));
            Assert.True(summary.HasBatch, "a settled rename is still describable");
            Assert.True(summary.Consumed);
            Assert.Equal(1, summary.Count);
            Assert.Equal(0, summary.RemainingCount);

            // A further undo over it answers rather than errors, and restores nothing.
            var again = await ext.UndoAsync(write, default);
            Assert.Equal(200, StatusOf(again));
            Assert.Equal(0, UndoValue(again).Undone);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public void UndoAndSummaryResponses_CarryExactlyTheseFields()
    {
        // A field appearing on either of these types moves the emitted wire document and, through it,
        // the generated frontend types — so it has to be a deliberate edit here rather than a side
        // effect noticed later. Transcribed by hand: a list derived from the type would agree with it
        // forever.
        //
        // The warning channel arrived with the stranded companion: an entry that came back minus its
        // sidecar is in no problem bucket, so before it the only record of a partial restore was the
        // host log, which the panel cannot read.
        //
        // Each channel is a COUNT paired with a SAMPLE because a batch reaches library size and this
        // response crosses to a browser. The pairing is transcribed here rather than checked as a
        // convention, so dropping a count — which would leave the panel reporting a sample's length as
        // the number of problems — is a failure here and not a quieter one in a user's library.
        Assert.Equal(
            [
                "Undone",
                "FailedCount", "FailedSample",
                "SkippedCount", "SkippedSample",
                "WarningCount", "WarningSample",
            ],
            typeof(UndoResult).GetProperties().Select(p => p.Name));
        Assert.Equal(
            ["FileId", "OldPath", "NewPath", "Reason"],
            typeof(UndoEntryError).GetProperties().Select(p => p.Name));
        Assert.Equal(
            ["FileId", "Detail"],
            typeof(UndoEntryWarning).GetProperties().Select(p => p.Name));

        // The summary is counts only — no collection member, and no path. That is what keeps the
        // response O(1) on a batch of any size and what keeps its coarse any-renamer-read gate honest,
        // so a collection appearing here is a permission question, not a formatting one.
        Assert.Equal(
            ["HasBatch", "Count", "RemainingCount", "UnrestorableCount", "WrittenAtUtcTicks", "Consumed"],
            typeof(LastBatchSummary).GetProperties().Select(p => p.Name));
        Assert.All(
            typeof(LastBatchSummary).GetProperties(),
            p => Assert.True(
                p.PropertyType == typeof(bool) || p.PropertyType == typeof(int) || p.PropertyType == typeof(long),
                $"LastBatchSummary.{p.Name} is {p.PropertyType.Name}: the summary carries scalars only."));
    }

    [Fact]
    public async Task Undo_EmptyLog_IsCleanNoOp()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, new CapturingEventBus());

            var result = await ext.UndoAsync(FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite), default);

            Assert.Equal(200, StatusOf(result));
            var undo = UndoValue(result);
            Assert.Equal(0, undo.Undone);
            Assert.Equal(0, undo.FailedCount);
            Assert.Equal(0, undo.SkippedCount);
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

            var (ext, store) = await ExtensionHarness.CreateWithSharedContextAsync(db, new CapturingEventBus());
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

    /// <summary>
    /// Seeds a second Video + VideoFile into an EXISTING folder, so two files share one batch and one
    /// directory. Returns (videoId, fileId).
    /// </summary>
    private static async Task<(int videoId, int fileId)> SeedVideoIntoFolderAsync(
        CoveContext db, int folderId, string basename, string title)
    {
        var video = new Video { Title = title, Organized = true };
        db.Set<Video>().Add(video);
        await db.SaveChangesAsync();

        var file = new VideoFile
        {
            Basename = basename,
            ParentFolderId = folderId,
            Format = basename[(basename.LastIndexOf('.') + 1)..],
            VideoId = video.Id,
        };
        db.Set<VideoFile>().Add(file);
        await db.SaveChangesAsync();
        return (video.Id, file.Id);
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
