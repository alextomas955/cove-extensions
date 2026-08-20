using Cove.Core.Entities;
using Cove.Data;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

/// <summary>
/// What the EXECUTOR writes into the journal: a rename far past the retired file-count ceiling is
/// journalled in full, and the sidecar delta a rename carried is recorded in the forward direction the
/// mover actually took — including the marker a rename that carried nothing leaves behind.
/// </summary>
/// <remarks>
/// This is the suite's only executor→journal seam. Every other site that mentions <c>SidecarsJson</c>
/// SUPPLIES it as test input, so nothing else can catch a broken write: the replayer trusts the row,
/// and a row written wrong restores the wrong thing while every downstream count still reads right.
/// <para>
/// Every assertion reads the journal back through the port over a real <c>CoveContext</c>, never an
/// in-memory collection: what a later undo can actually offer is whatever the table still holds, and a
/// fake's memory agreeing with itself would say nothing about that.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class JournalRenameUndoTracerTests
{
    private static readonly DateTime Opened = new(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ABatchFarPastTheRetiredCeiling_IsJournalledInFull()
    {
        // 5000 files was the ceiling above which a batch was not journalled AT ALL, so the largest
        // renames — the ones a user is least able to put right by hand — were the ones left with no undo.
        // The rows are appended through the port rather than by renaming 5001 real files: what is under
        // test is that no count-based branch survives on the write path, and a case slow enough to be
        // skipped is not a case.
        const int pastTheOldCeiling = 5001;

        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var journal = new CoveRevertJournal(db);
            await SeedBatchAsync(journal, "huge-run", pastTheOldCeiling, Opened);

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            Assert.Equal(pastTheOldCeiling, batch!.Rows.Count);
            // One row per file, each with its own place in the batch — a count alone would not rule out
            // rows sharing an identity and therefore being un-retirable one at a time.
            Assert.Equal(pastTheOldCeiling, batch.Rows.Select(r => r.Seq).Distinct().Count());

            var summary = await journal.ReadUndoTargetAsync();
            Assert.NotNull(summary);
            Assert.Equal(pastTheOldCeiling, summary!.Value.OriginalCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARenameThatCarriedSidecars_JournalsTheMovesAndTheCaptionsOriginalFilename()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "My Film");

            var caption = new VideoCaption
            {
                FileId = fileId,
                Filename = "clip.en.vtt",
                LanguageCode = "en",
                CaptionType = "vtt",
            };
            db.Set<VideoCaption>().Add(caption);
            await db.SaveChangesAsync();

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.en.vtt"), "caption");
            File.WriteAllText(Path.Combine(dir.Root, "clip.srt"), "subs");

            var options = new RenamerOptions { FilenameTemplate = "$title", AssociatedExtensions = ["srt"] };
            var row = await RenameAndReadRowAsync(db, "sidecar-run", videoId, options);

            Assert.True(RevertDelta.TryParse(row.SidecarsJson, out var delta));

            // Both kinds ride in the delta: the database-tracked caption and the configured neighbour,
            // each in the FORWARD direction the mover actually took.
            Assert.Collection(
                delta.Sidecars,
                s =>
                {
                    Assert.Equal(folderPath + "/clip.en.vtt", s.FromPath);
                    Assert.Equal(folderPath + "/My Film.en.vtt", s.ToPath);
                },
                s =>
                {
                    Assert.Equal(folderPath + "/clip.srt", s.FromPath);
                    Assert.Equal(folderPath + "/My Film.srt", s.ToPath);
                });

            // The ORIGINAL stored filename, which is the value an undo writes back — the renamed one is
            // already in the database and would tell a reverse replay nothing.
            var journalled = Assert.Single(delta.Captions);
            Assert.Equal(caption.Id, journalled.CaptionId);
            Assert.Equal("clip.en.vtt", journalled.OriginalFilename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASidecarThatWasPlannedButDidNotMove_IsAbsentFromTheJournalledDelta()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "My Film");

            db.Set<VideoCaption>().Add(new VideoCaption
            {
                FileId = fileId,
                Filename = "clip.en.vtt",
                LanguageCode = "en",
                CaptionType = "vtt",
            });
            await db.SaveChangesAsync();

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.en.vtt"), "caption");
            File.WriteAllText(Path.Combine(dir.Root, "clip.srt"), "subs");
            // The neighbour's destination is already taken, so the mover's skip-not-clobber rule leaves
            // it where it is. It was PLANNED and it did not move — the distinction no string arithmetic
            // can recover afterwards, and the reason the delta is recorded rather than recomputed.
            File.WriteAllText(Path.Combine(dir.Root, "My Film.srt"), "someone else's subs");

            var options = new RenamerOptions { FilenameTemplate = "$title", AssociatedExtensions = ["srt"] };
            var row = await RenameAndReadRowAsync(db, "partial-sidecar-run", videoId, options);

            Assert.True(RevertDelta.TryParse(row.SidecarsJson, out var delta));

            var moved = Assert.Single(delta.Sidecars);
            Assert.Equal(folderPath + "/clip.en.vtt", moved.FromPath);
            Assert.DoesNotContain(delta.Sidecars, s => s.FromPath.EndsWith("clip.srt", StringComparison.Ordinal));
            Assert.Single(delta.Captions);

            // And the file the mover refused to clobber is untouched, which is what made it a non-move.
            Assert.Equal("someone else's subs", File.ReadAllText(Path.Combine(dir.Root, "My Film.srt")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARenameThatCarriedNothing_JournalsTheEmptyMarkerRatherThanAPlaceholder()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");

            var row = await RenameAndReadRowAsync(
                db, "bare-run", videoId, new RenamerOptions { FilenameTemplate = "$title" });

            // The same bytes a row written before deltas existed carries: one state, not two.
            Assert.Equal("", row.SidecarsJson);
            Assert.False(RevertDelta.TryParse(row.SidecarsJson, out var delta));
            Assert.True(delta.IsEmpty);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Plans and executes a real rename of <paramref name="videoId"/>, then reads the single row it
    /// journalled back out of the table — never out of an in-memory mirror of it.
    /// </summary>
    private static async Task<RevertRow> RenameAndReadRowAsync(
        CoveContext db, string runId, int videoId, RenamerOptions options)
    {
        var port = new CoveRenamerDataPort(db);
        using var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync(runId, RenamerFileKind.Video, Opened);

        var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, RouteLookupsFixtures.RoutingNeutral, default);
        var forward = await new RenamerExecutor(port, new CapturingEventBus(), journal, runId, new DiskMover())
            .ExecuteAsync(plan, options, default);
        Assert.Single(forward.Renamed);

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
        Assert.NotNull(batch);
        return Assert.Single(batch!.Rows);
    }

    private static async Task SeedBatchAsync(
        CoveRevertJournal journal, string runId, int rows, DateTime openedAt)
    {
        await journal.BeginBatchAsync(runId, RenamerFileKind.Video, openedAt);
        for (int i = 1; i <= rows; i++)
        {
            await journal.AppendAsync(
                new RevertRow(runId, Seq: 0, EntityId: 100 + i, FileId: 200 + i, $"/media/old/{i}.mkv", ""));
        }
    }
}
