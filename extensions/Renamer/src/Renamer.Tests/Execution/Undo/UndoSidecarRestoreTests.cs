using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Undo;

/// <summary>
/// Drives a real rename and then a real undo over one <see cref="CoveContext"/> and one
/// <see cref="TempDir"/>, to prove undo puts BOTH sidecar kinds back — the database-tracked caption and
/// the configured same-stem neighbour — and to pin what happens when one of them cannot go back.
/// </summary>
/// <remarks>
/// Every case asserts on BOTH halves: the file on disk AND the row in the database. A case that
/// checked only one would pass while the two disagreed, which is precisely the failure the
/// moved-only rule exists to prevent — a caption whose stored filename was rewritten to a name no
/// file on disk has.
/// <para>
/// The delta each undo replays is the one the FORWARD run journalled, read back out of the table. It
/// is never hand-built here, because a hand-built delta would prove the replayer can follow
/// instructions rather than that the two halves agree.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class UndoSidecarRestoreTests
{
    private static readonly DateTime Opened = new(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ANeighbourThatRodeAlong_ComesBackToItsOriginalNameAndFolder()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "My Film");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.srt"), "subs");

            var options = new RenamerOptions { FilenameTemplate = "$title", AssociatedExtensions = ["srt"] };
            var run = await RenameThenUndoAsync(db, "neighbour-run", videoId, options);

            Assert.Equal(1, run.Undone);
            Assert.Empty(run.Failed);
            Assert.Empty(run.Skipped);

            Assert.True(File.Exists(Path.Combine(dir.Root, "clip.srt")), "the neighbour is back at its original name");
            Assert.Equal("subs", File.ReadAllText(Path.Combine(dir.Root, "clip.srt")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "My Film.srt")));

            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("clip.mkv", basename);
            Assert.Equal(folderPath + "/clip.mkv", path);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ATrackedCaption_ComesBackOnDiskAndItsStoredFilenameIsWrittenBack()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "My Film");
            int captionId = await SeedCaptionAsync(db, fileId, "clip.en.vtt");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.en.vtt"), "caption");

            var options = new RenamerOptions { FilenameTemplate = "$title" };
            var run = await RenameThenUndoAsync(db, "caption-run", videoId, options);

            Assert.Equal(1, run.Undone);

            Assert.True(File.Exists(Path.Combine(dir.Root, "clip.en.vtt")), "the caption file is back");
            Assert.Equal("caption", File.ReadAllText(Path.Combine(dir.Root, "clip.en.vtt")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "My Film.en.vtt")));

            Assert.Equal("clip.en.vtt", await ReadCaptionFilenameAsync(db, captionId));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ANeighbourWhoseOriginalSlotIsTaken_LeavesTheEntryRestoredWithAWarningNamingIt()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "My Film");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.srt"), "subs");

            var options = new RenamerOptions { FilenameTemplate = "$title", AssociatedExtensions = ["srt"] };
            var run = await RenameThenUndoAsync(db, "stranded-run", videoId, options, betweenRenameAndUndo: () =>
            {
                // Something took the neighbour's original slot while the rename stood. The reverse move
                // must refuse to clobber it — and that refusal must not strand the film's recovery on a
                // subtitle.
                File.WriteAllText(Path.Combine(dir.Root, "clip.srt"), "someone else's subs");
                return Task.CompletedTask;
            });

            Assert.Equal(1, run.Undone);
            Assert.Empty(run.Failed);
            Assert.Empty(run.Skipped);

            // The warning names the slot the reverse move refused to clobber, which is the sidecar's
            // own original path — the one thing that identifies which companion did not come back.
            var warning = Assert.Single(run.Warnings);
            Assert.Equal(fileId, warning.FileId);
            Assert.Contains("clip.srt", warning.Detail, StringComparison.Ordinal);

            // The media file is back and its row agrees — which is exactly what undo promises.
            Assert.True(File.Exists(Path.Combine(dir.Root, "clip.mkv")));
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("clip.mkv", basename);
            Assert.Equal(folderPath + "/clip.mkv", path);

            // And the occupant was never clobbered; the stranded sidecar stayed at its renamed name.
            Assert.Equal("someone else's subs", File.ReadAllText(Path.Combine(dir.Root, "clip.srt")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.srt")), "the stranded sidecar stays where it is");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ACaptionWhoseFileCouldNotComeBack_KeepsItsCurrentStoredFilename()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "My Film");
            int captionId = await SeedCaptionAsync(db, fileId, "clip.en.vtt");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.en.vtt"), "caption");

            var options = new RenamerOptions { FilenameTemplate = "$title" };
            var run = await RenameThenUndoAsync(db, "caption-stranded-run", videoId, options, betweenRenameAndUndo: () =>
            {
                File.WriteAllText(Path.Combine(dir.Root, "clip.en.vtt"), "someone else's caption");
                return Task.CompletedTask;
            });

            Assert.Equal(1, run.Undone);
            Assert.Single(run.Warnings);

            // The caption's FILE is still at the renamed name, so its row must still say so. Writing the
            // original name back here would leave the database naming a file that does not exist.
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.en.vtt")));
            Assert.Equal("My Film.en.vtt", await ReadCaptionFilenameAsync(db, captionId));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ACaptionRowThatIsNotAlreadyTracked_StillFollowsTheRenameAndComesBack()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "My Film");
            int captionId = await SeedCaptionAsync(db, fileId, "clip.en.vtt");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.en.vtt"), "caption");

            // The one line that makes this case measure the code rather than the fixture. Seeding a
            // caption through this context leaves it in the change tracker, and relationship fix-up
            // then populates the file's Captions navigation — state PRODUCTION never has, because
            // every read the port makes is AsNoTracking and each batch worker saves through a context
            // that has loaded nothing. Every other case here inherits that fixture-supplied state, so
            // a rename whose caption write silently did nothing passed all of them.
            db.ChangeTracker.Clear();

            string afterRename = "";
            var run = await RenameThenUndoAsync(
                db, "caption-untracked-run", videoId, new RenamerOptions { FilenameTemplate = "$title" },
                betweenRenameAndUndo: async () => afterRename = await ReadCaptionFilenameAsync(db, captionId));

            // The forward half is asserted on its own: a round trip that never wrote in either
            // direction ends on the original name too, and would read as a pass.
            Assert.Equal("My Film.en.vtt", afterRename);

            Assert.Equal(1, run.Undone);
            Assert.Equal("clip.en.vtt", await ReadCaptionFilenameAsync(db, captionId));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARenameThatCarriedNothing_UndoesExactlyAsItAlwaysDid()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");

            var run = await RenameThenUndoAsync(
                db, "bare-undo-run", videoId, new RenamerOptions { FilenameTemplate = "$title" });

            Assert.Equal(1, run.Undone);
            Assert.Empty(run.Failed);
            Assert.Empty(run.Skipped);
            Assert.Empty(run.Warnings);

            Assert.True(File.Exists(Path.Combine(dir.Root, "clip.mkv")));
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("clip.mkv", basename);
            Assert.Equal(folderPath + "/clip.mkv", path);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs a real rename of <paramref name="videoId"/>, optionally disturbs the directory, then
    /// reverse-replays the batch the rename journalled — reading that batch back out of the table, so
    /// what the replayer acts on is what a production undo would be handed.
    /// </summary>
    private static async Task<UndoReplayer.UndoRunResult> RenameThenUndoAsync(
        CoveContext db, string runId, int videoId, RenamerOptions options,
        Func<Task>? betweenRenameAndUndo = null)
    {
        var port = new CoveRenamerDataPort(db);
        using var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync(runId, RenamerFileKind.Video, Opened);

        var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, RouteLookupsFixtures.RoutingNeutral, default);
        var forward = await new RenamerExecutor(port, new CapturingEventBus(), journal, runId, new DiskMover())
            .ExecuteAsync(plan, options, default);
        Assert.Single(forward.Renamed);

        if (betweenRenameAndUndo is not null)
        {
            await betweenRenameAndUndo();
        }

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
        Assert.NotNull(batch);

        return await new UndoReplayer(port, new CapturingEventBus(), new DiskMover()).RevertAsync(batch!);
    }

    private static async Task<int> SeedCaptionAsync(CoveContext db, int fileId, string filename)
    {
        var caption = new VideoCaption
        {
            FileId = fileId,
            Filename = filename,
            LanguageCode = "en",
            CaptionType = "vtt",
        };
        db.Set<VideoCaption>().Add(caption);
        await db.SaveChangesAsync();
        return caption.Id;
    }

    private static async Task<string> ReadCaptionFilenameAsync(CoveContext db, int captionId)
    {
        var caption = await db.Set<VideoCaption>().AsNoTracking().FirstAsync(c => c.Id == captionId);
        return caption.Filename;
    }
}
