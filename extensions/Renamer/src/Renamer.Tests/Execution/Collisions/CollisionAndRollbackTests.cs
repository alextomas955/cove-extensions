using Cove.Core.Entities;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Collisions;

/// <summary>
/// The collision/rollback safety spine, exercised against a real SQLite
/// <see cref="Cove.Data.CoveContext"/> and a real <see cref="TempDir"/>: what happens when a rename's
/// target name is already taken (by the source itself, by a different file, or by a DB row), and what
/// the executor must restore when a save fails AFTER the disk move has already happened.
/// </summary>
/// <remarks>
/// SQLite rather than EF-InMemory is load-bearing throughout: InMemory enforces neither the
/// <c>(ParentFolderId, Basename)</c> unique index nor transaction rollback, so the backstop cases below
/// would FALSE-GREEN on it.
/// <para>
/// This class does NOT absorb <c>Execution/Undo/RollbackTests</c>, which shares the
/// save-fails-after-move scenario and pins a disjoint set of internals — outcome classification, the
/// absence of a journal row and of an event, plus a positive control — over a pure
/// <c>FakeRenamerDataPort</c> with no live database. It is also a bare-leg source file, so folding it
/// into this cove-dependent class would delete it from the leg CI actually runs.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class CollisionAndRollbackTests
{
    // ── case-only renames ─────────────────────────────────────────────────────
    //
    // Neither case is platform-gated, and that is deliberate. The clean-rename outcome is
    // platform-UNIVERSAL but reached by two different mechanisms: where the volume folds case (Windows,
    // macOS) File.Exists of the case-variant target is TRUE and the fix is that PathOps.PathsEqual
    // recognizes it as the source's own slot; where the volume is case-sensitive (Linux ext4)
    // File.Exists is simply false, so no collision path is entered at all. Gating this on IsWindows()
    // hid the macOS defect it was written to catch — do not re-gate it.

    /// <summary>
    /// A pure case-fix rename — where the only thing occupying the target name is the SOURCE file
    /// itself — completes as a clean <see cref="RenamerStatus.Rename"/>, NOT a needlessly suffixed
    /// <c>Movie (1).mkv</c> and NOT a collision skip. Uses the real
    /// <see cref="CoveRenamerDataPort"/> (not the collision-blind one) so the disk-side
    /// <c>File.Exists</c> check is the one under test.
    /// </summary>
    [Fact]
    public async Task CaseOnlyRenamer_OfFileOntoItself_IsCleanRenamer_NotSuffixed()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "movie.mkv", "My Film");

            // Disk: only the lower-case source exists. Where the volume folds case, File.Exists of the
            // case-variant target is True — but it is the SOURCE occupying its own slot, not a clobber.
            // Where it does not, File.Exists is false and the collision loop never runs. Same outcome.
            File.WriteAllText(Path.Combine(dir.Root, "movie.mkv"), "movie-bytes");

            // Hand-built in-place plan: movie.mkv → Movie.mkv (case-only), so the executor's collision
            // seam is exercised directly and deterministically (no planner in the way).
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, folderPath + "/movie.mkv", folderPath + "/Movie.mkv",
                    RenamerStatus.Rename, "Movie.mkv", folderPath),
            ]);

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // Clean Renamer: exactly one renamed, nothing skipped or failed, and the new name is the
            // case-corrected target — NOT a suffixed Movie (1).mkv.
            var renamedItem = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Rename, renamedItem.Status);
            Assert.Empty(result.Skipped);
            Assert.Empty(result.Failed);
            Assert.EndsWith("Movie.mkv", renamedItem.NewPath);
            Assert.DoesNotContain("(1)", renamedItem.NewPath);

            // DB read-back confirms the corrected basename (asserted via the row, not a case-blind
            // File.Exists which would be True for both spellings on this volume).
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("Movie.mkv", basename);
            Assert.Equal(folderPath + "/Movie.mkv", path);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The guard on widening the case rule: a DIFFERENT file already at the case-variant target name
    /// still collides, so a third source renamed onto it is suffixed or skipped, never clobbering.
    /// </summary>
    [Fact]
    public async Task DifferentFileAtCaseVariantName_StillCollides_NoClobber()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');

            // Seed three distinct files in one folder: the lower-case "movie.mkv", a DIFFERENT
            // "Movie.mkv" already occupying the case-variant name, and the source we will renamer.
            // This is the guard on widening the case rule (stated at PathOps.PathsEqual, with the
            // APFS/CIFS caveats): ignoring case for SELF-path equality must not let a different
            // file's name be treated as free.
            var (folderId, videoId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "movie.mkv", "My Film");
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "Movie.mkv");
            var sourceId = await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "other.mkv");

            File.WriteAllText(Path.Combine(dir.Root, "movie.mkv"), "lower-bytes");
            File.WriteAllText(Path.Combine(dir.Root, "Movie.mkv"), "different-file-bytes");
            File.WriteAllText(Path.Combine(dir.Root, "other.mkv"), "source-bytes");

            // Renamer the THIRD source onto the case-variant name a DIFFERENT file already holds.
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(sourceId, folderPath + "/other.mkv", folderPath + "/Movie.mkv",
                    RenamerStatus.Rename, "Movie.mkv", folderPath),
            ]);

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // No clobber: the source did NOT land on the existing Movie.mkv. It was either suffixed to a
            // free name (Renamed, not "Movie.mkv") or skip-collisioned.
            if (result.Renamed.Count == 1)
            {
                Assert.NotEqual(folderPath + "/Movie.mkv", result.Renamed[0].NewPath.Replace('\\', '/'));
                Assert.Empty(result.Failed);
            }
            else
            {
                var skipped = Assert.Single(result.Skipped);
                Assert.Equal(RenamerStatus.SkipCollision, skipped.Status);
                Assert.Empty(result.Renamed);
            }

            // The pre-existing DIFFERENT file at Movie.mkv is untouched — its bytes survive intact.
            Assert.Equal("different-file-bytes", File.ReadAllText(Path.Combine(dir.Root, "Movie.mkv")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // ── the unique-index backstop ─────────────────────────────────────────────

    /// <summary>
    /// The <c>(ParentFolderId, Basename)</c> UNIQUE index is the final safety net behind the proactive
    /// suffix loop: with the DB collision pre-check BYPASSED via <see cref="CollisionBlindDataPort"/> the
    /// save itself hits the index, the executor's catch fires, and the disk file is rolled back.
    /// </summary>
    /// <remarks>
    /// Kept under its exact name deliberately. This test was the surviving coverage that justified
    /// deleting <c>Planner/IRenamerDataPortSeamTests</c> — those cases asserted a test double's
    /// own seeded returns, whereas this one drives the real relational index. Renaming or dropping it
    /// would retroactively unpin that deletion.
    /// </remarks>
    [Fact]
    public async Task DuplicateBasenameSave_ThrowsOnUniqueIndex_CaughtAndRolledBack()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");
            var fileB = await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "b.mkv");

            // Disk: only "a.mkv" exists ("b.mkv" the DB-occupied target is NOT on disk, so the disk
            // move SUCCEEDS and the save is what hits the unique index).
            string oldA = Path.Combine(dir.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");

            // Hand-build a plan that renames a → b (the DB-taken name). The blind port stops the
            // executor's exec-time re-check from suffixing it away.
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/b.mkv",
                    RenamerStatus.Rename, "b.mkv", folderPath),
            ]);

            var port = new CollisionBlindDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // The save threw on the unique index → item failed, caught (not propagated).
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Empty(result.Renamed);

            // Disk rolled back: "a.mkv" restored, "b.mkv" not left on disk; no event published.
            Assert.True(File.Exists(oldA), "the disk file must be rolled back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            Assert.False(File.Exists(Path.Combine(dir.Root, "b.mkv")), "the moved file must not linger at the new path");
            Assert.Empty(bus.Published);

            // DB rows unchanged.
            var (basenameA, _) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            var (basenameB, _) = await ExecutorTestSeed.ReadFileAsync(db, fileB);
            Assert.Equal("a.mkv", basenameA);
            Assert.Equal("b.mkv", basenameB);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // ── save-fails-after-move rollback ────────────────────────────────────────

    /// <summary>
    /// The disk move SUCCEEDS but the subsequent <c>SaveChangesAsync</c> THROWS: the file is back at its
    /// ORIGINAL on-disk path and the DB row still carries the OLD basename — disk and DB consistent.
    /// </summary>
    /// <remarks>
    /// The failure reason naming the rollback is what proves the CATCH produced the terminal state
    /// rather than the move never having happened: the only path that reaches <c>SaveChangesAsync</c> is
    /// after a successful move, and post-rollback the file cannot be observed at the new path.
    /// </remarks>
    [Fact]
    public async Task SaveFailsAfterMove_FileRestoredToOldPath_DbRowUnchanged()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");
            // A second row occupies "taken.mkv" so the save of a→taken hits the unique index.
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "taken.mkv");

            // Disk: "a.mkv" exists; "taken.mkv" does NOT (so the disk move succeeds first).
            string oldA = Path.Combine(dir.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newPath = Path.Combine(dir.Root, "taken.mkv");
            Assert.False(File.Exists(newPath), "precondition: disk target must be free so the MOVE happens before the save");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/taken.mkv",
                    RenamerStatus.Rename, "taken.mkv", folderPath),
            ]);

            var port = new CollisionBlindDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(port, bus, journal, "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // The save threw after the move → item failed with a rollback reason (proving the catch,
            // i.e. the move had ALREADY happened before the save error — not a pre-move skip).
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(journal.Rows);       // no success row written
            Assert.Empty(bus.Published);      // no event for a failed item

            // (a) the file is restored to its ORIGINAL path with original content.
            Assert.True(File.Exists(oldA), "file must be rolled back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            // and is NOT left at the new path.
            Assert.False(File.Exists(newPath), "rolled-back file must not linger at the new path");

            // (c) the DB row still has the OLD basename — disk and DB consistent.
            var (basenameA, pathA) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("a.mkv", basenameA);
            Assert.Equal(folderPath + "/a.mkv", pathA);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>The sidecar variant of the case above: a moved caption sidecar is restored too.</summary>
    [Fact]
    public async Task SaveFailsAfterMove_SidecarAlsoRestored()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "taken.mkv");

            // Seed a caption sidecar on file A.
            db.Set<VideoCaption>().Add(new VideoCaption { FileId = fileA, Filename = "a.en.vtt", LanguageCode = "en", CaptionType = "vtt" });
            await db.SaveChangesAsync();

            string oldA = Path.Combine(dir.Root, "a.mkv");
            string oldCap = Path.Combine(dir.Root, "a.en.vtt");
            File.WriteAllText(oldA, "A-bytes");
            File.WriteAllText(oldCap, "caption");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/taken.mkv",
                    RenamerStatus.Rename, "taken.mkv", folderPath),
            ]);

            var executor = new RenamerExecutor(
                new CollisionBlindDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            Assert.Single(result.Failed);
            // Both the primary file AND the moved caption sidecar are restored to their old paths.
            Assert.True(File.Exists(oldA), "primary file restored");
            Assert.True(File.Exists(oldCap), "sidecar caption restored");
            Assert.Equal("caption", File.ReadAllText(oldCap));
            Assert.False(File.Exists(Path.Combine(dir.Root, "taken.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "taken.en.vtt")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The post-save consistency-assertion branch: the DB save SUCCEEDS (commits the new basename) but
    /// the runtime "recomputed Path == on-disk path" assertion FAILS. The executor must roll the disk
    /// back to the OLD path through the same mover the move used — NOT leave the file abandoned at the
    /// new path with no undo record (the pre-fix bug). Assert: (a) the item is Failed with a
    /// path-mismatch + rolled-back reason, (b) the file is back at its OLD on-disk path, (c) no
    /// revert-log row and no event were written for it.
    /// </summary>
    [Fact]
    public async Task SaveSucceedsButRecomputedPathMismatch_FileRolledBack_NoRevertLog()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");

            // Disk: "a.mkv" exists; the target "b.mkv" is free so the disk move succeeds first.
            string oldA = Path.Combine(dir.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newPath = Path.Combine(dir.Root, "b.mkv");
            Assert.False(File.Exists(newPath), "precondition: disk target must be free so the MOVE happens before the save");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/b.mkv",
                    RenamerStatus.Rename, "b.mkv", folderPath),
            ]);

            // Port that COMMITS the real save (new basename persisted) but reports a RecomputedPath that
            // does NOT match the on-disk destination, tripping the post-save assertion.
            var port = new MismatchedRecomputedPathDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(port, bus, journal, "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // (a) the item is Failed, and the reason names BOTH the path mismatch and the rollback.
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("recomputed Path", failedItem.Reason);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);

            // (c) no journal row and no event for a failed item.
            Assert.Empty(journal.Rows);
            Assert.Empty(bus.Published);

            // (b) the file is rolled back to its OLD path with original content, and NOT at the new path.
            Assert.True(File.Exists(oldA), "file must be rolled back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            Assert.False(File.Exists(newPath), "rolled-back file must not linger at the new path");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The suffix this executor appends to free a taken slot pushes the absolute path past
    /// <see cref="RenamerOptions.FullPathMax"/>, so the item is refused rather than written.
    /// </summary>
    /// <remarks>
    /// Why this belongs here and not with the planner's own length cases: the plan arrives ALREADY at the
    /// budget and legal, and this executor's collision loop is what lengthens it. A plan-time re-measure
    /// cannot see this — it measures a name that fits — so the two sites refuse independently and each
    /// needs its own case. The refusal lands after the confirm gate, which is why the second half of the
    /// assertions matters more than the status: nothing may be left on disk or in the database.
    /// </remarks>
    [Fact]
    public async Task SuffixPushingThePathPastTheBudget_IsSkippedTooLong_NothingMoves()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');

            // The budget is DERIVED, never a literal: TempDir.Root sits under the machine's temp path and
            // its length differs per machine and per OS, so a hand-written number would sit beside the
            // boundary rather than on it. This is exactly the length of the unsuffixed target — the plan
            // below is legal at plan time, and only the suffix breaks it.
            var options = new RenamerOptions { FullPathMax = (folderPath + "/taken.mkv").Length };

            var (folderId, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "a.mkv"), "a-bytes");

            // A DIFFERENT file already holds the target name in the database, so the collision loop runs
            // and appends " (1)" — four characters past a budget the unsuffixed name met exactly.
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "taken.mkv");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/taken.mkv",
                    RenamerStatus.Rename, "taken.mkv", folderPath),
            ]);

            var port = new CoveRenamerDataPort(db);
            var executor = new RenamerExecutor(
                port, new CapturingEventBus(), new FakeRevertJournal(), "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, options, default);

            var skipped = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipTooLong, skipped.Status);
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);

            // The half that makes this a safety pin rather than a status assertion: the refusal happens
            // before anything is touched, so the source survives, the suffixed name was never created, and
            // the database still names the original.
            Assert.True(File.Exists(Path.Combine(dir.Root, "a.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "taken (1).mkv")));
            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("a.mkv", basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Test-only port: performs the REAL save (so the DB row genuinely commits the new basename), then
    /// returns a <see cref="SavedFile"/> whose RecomputedPath is deliberately wrong,
    /// so the executor's post-save "recomputed Path == on-disk path" assertion fails on the success path.
    /// </summary>
    private sealed class MismatchedRecomputedPathDataPort(Cove.Data.CoveContext db) : CoveRenamerDataPort(db)
    {
        public override async Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
            IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
        {
            var saved = await base.ApplyAndSaveAsync(mutations, ct);
            return [.. saved.Select(s => new SavedFile(s.FileId, s.RecomputedPath + ".WRONG"))];
        }
    }
}
