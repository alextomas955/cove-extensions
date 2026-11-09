using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

// One video per stem, really renamed into one journal batch, so the batch holds one row per file and
// an undo over it is a statement about rows rather than about batches.
//
// The forward half runs through the planner and the executor directly rather than through the batch
// endpoint: that endpoint fans its files out across per-worker scopes, and every scope here resolves
// the one seeded context.
internal static class RenamedBatchFixture
{
    // One file of the batch: the ids the journal records and the two paths it moved between.
    internal sealed record Renamed(int EntityId, int FileId, string OldFull, string NewFull);

    internal static Task<(global::Renamer.Renamer Extension, IReadOnlyList<Renamed> Files)> CreateAsync(
        TempDir dir, DbContext db, string runId, int count, int? undoPageSize = null)
        => CreateAsync(dir, db, runId, [.. Enumerable.Range(1, count).Select(i => i.ToString(
            System.Globalization.CultureInfo.InvariantCulture))], undoPageSize);

    internal static async Task<(global::Renamer.Renamer Extension, IReadOnlyList<Renamed> Files)> CreateAsync(
        TempDir dir, DbContext db, string runId, IReadOnlyList<string> stems, int? undoPageSize = null)
    {
        string folderPath = dir.Root.Replace('\\', '/');
        var folder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync();

        var files = new List<Renamed>(stems.Count);
        foreach (string stem in stems)
        {
            var video = new Video { Title = $"{stem} film", Organized = true };
            db.Set<Video>().Add(video);
            await db.SaveChangesAsync();

            var file = new VideoFile
            {
                Basename = $"raw {stem}.mkv",
                ParentFolderId = folder.Id,
                Format = "mkv",
                VideoId = video.Id,
            };
            db.Set<VideoFile>().Add(file);
            await db.SaveChangesAsync();

            string oldFull = Path.Combine(dir.Root, $"raw {stem}.mkv");
            await File.WriteAllTextAsync(oldFull, $"{stem}-bytes");
            files.Add(new Renamed(
                video.Id, file.Id, oldFull, Path.Combine(dir.Root, $"{stem} film.mkv")));
        }

        var options = new RenamerOptions { FilenameTemplate = "$title" };
        var port = new CoveRenamerDataPort(db);

        // Opened now rather than at a fixed moment: the undo refuses a batch past its retention window.
        await using (var journal = new CoveRevertJournal(db))
        {
            await journal.BeginBatchAsync(runId, runId, RenamerFileKind.Video, DateTime.UtcNow);
            foreach (var renamed in files)
            {
                var plan = await new RenamerPlanner(port)
                    .PlanAsync(RenamerFileKind.Video, renamed.EntityId, options, default);
                var forward = await new RenamerExecutor(port, new CapturingEventBus(), journal, runId)
                    .ExecuteAsync(plan, options, default);
                Assert.Single(forward.Renamed);
            }
        }

        foreach (var renamed in files)
        {
            Assert.True(File.Exists(renamed.NewFull), $"forward rename landed at {renamed.NewFull}");
            Assert.False(File.Exists(renamed.OldFull));
        }

        var (extension, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options: options);
        if (undoPageSize is { } pageSize)
        {
            extension.UndoPageSize = pageSize;
        }

        return (extension, files);
    }
}
