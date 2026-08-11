using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Execution.Journal;

/// <summary>
/// The journal on the real lifecycle path: a real rename writes rows a real undo reads back and
/// restores, a second batch leaves the first one's rows alone, and a rename far past the retired
/// file-count ceiling is journalled in full.
/// </summary>
/// <remarks>
/// Every assertion reads the journal back through the port over a real <c>CoveContext</c>, never an
/// in-memory collection: what a later undo can actually offer is whatever the table still holds, and a
/// fake's memory agreeing with itself would say nothing about that.
/// </remarks>
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class JournalRenameUndoTracerTests
{
    private static readonly DateTime Opened = new(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ARealRenameJournalsARow_AndARealUndoRestoresTheFileAndRetiresIt()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Offset the Video id sequence so the video id differs from its file's id: the row carries
            // both, and a row that confused them would still look right while restoring the wrong thing.
            db.Set<Video>().Add(new Video { Title = "decoy", Organized = true });
            await db.SaveChangesAsync();
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");
            Assert.NotEqual(videoId, fileId);

            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            const string runId = "tracer-run";
            var port = new CoveRenamerDataPort(db);
            var journal = new CoveRevertJournal(db);
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            await journal.BeginBatchAsync(runId, RenamerFileKind.Video, Opened);

            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            var forward = await new RenamerExecutor(port, new CapturingEventBus(), journal, runId, new DiskMover())
                .ExecuteAsync(plan, options, default);

            Assert.Single(forward.Renamed);
            Assert.True(File.Exists(newFull));

            // One ROW in the table — not one entry in a list the executor happened to keep.
            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            Assert.Equal(runId, batch!.RunId);
            Assert.Equal(RenamerFileKind.Video, batch.Kind);
            var row = Assert.Single(batch.Rows);
            Assert.Equal(runId, row.RunId);
            Assert.Equal(videoId, row.EntityId);
            Assert.Equal(fileId, row.FileId);
            Assert.Equal(folderPath + "/raw clip.mkv", row.OldPath);

            // The undo runs through the endpoint rather than through a hand-built UndoReplayer call, so
            // the row is retired by the code that will retire it in production. The endpoint builds the
            // same real replayer over the same batch this test just read.
            var undoBus = new CapturingEventBus();
            var ext = await BuildExtensionAsync(db, undoBus, options);
            var undo = UndoValue(await ext.UndoAsync(
                FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite), default));

            Assert.Equal(1, undo.Undone);
            Assert.Empty(undo.Failed);
            Assert.Empty(undo.Skipped);

            // Restored on disk AND in the database — either one alone would leave the two disagreeing.
            Assert.True(File.Exists(oldFull), "the file is back at its original path");
            Assert.False(File.Exists(newFull), "and no longer at the renamed one");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw clip.mkv", basename);
            Assert.Equal(folderPath + "/raw clip.mkv", path);

            // Nothing left to offer, and yet the batch can still describe itself: the row went away as
            // the file came back, while the count of what the run journalled never moves.
            Assert.Null(await JournalPageReader.ReadWholeUndoTargetAsync(journal));
            var summary = await journal.ReadUndoTargetAsync();
            Assert.NotNull(summary);
            Assert.Equal(runId, summary!.Value.RunId);
            Assert.Equal(1, summary.Value.OriginalCount);
            Assert.Equal(1, summary.Value.RestoredCount);
            Assert.Equal(0, summary.Value.Remaining);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task OpeningASecondBatch_LeavesTheFirstBatchsRowsWhereTheyWere()
    {
        // The defect this closes: under the single-blob journal, opening a batch REPLACED the stored
        // value, so one background auto-rename — which opens its own batch per metadata edit — silently
        // destroyed the undo record of a deliberate run that had just moved hundreds of files.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var journal = new CoveRevertJournal(db);

            await SeedBatchAsync(journal, "deliberate-run", rows: 3, Opened);
            await SeedBatchAsync(journal, "background-edit", rows: 1, Opened.AddMinutes(5));

            // The newest batch is what an undo is offered first…
            var newest = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(newest);
            Assert.Equal("background-edit", newest!.RunId);
            var backgroundRow = Assert.Single(newest.Rows);

            // …and once it is spent, the earlier run is still there, whole.
            await journal.DeleteRowAsync(backgroundRow.RunId, backgroundRow.Seq, unrestorable: false);

            var earlier = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(earlier);
            Assert.Equal("deliberate-run", earlier!.RunId);
            Assert.Equal(3, earlier.Rows.Count);
            Assert.Equal(
                ["/media/old/3.mkv", "/media/old/2.mkv", "/media/old/1.mkv"],
                earlier.Rows.Select(r => r.OldPath));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

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

        var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
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

    /// <summary>
    /// Wires the extension over the seeded context so <c>/undo</c> resolves the same database this test
    /// journalled into, mirroring <c>UndoEndpointTests</c>.
    /// </summary>
    private static async Task<global::Renamer.Renamer> BuildExtensionAsync(
        CoveContext db, IEventBus bus, RenamerOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        services.AddSingleton(bus);

        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options);

        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(services.BuildServiceProvider());
        return ext;
    }

    private static UndoResult UndoValue(IResult result) =>
        Assert.IsType<UndoResult>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);
}
