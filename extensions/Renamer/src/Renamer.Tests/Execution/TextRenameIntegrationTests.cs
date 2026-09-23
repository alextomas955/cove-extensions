using Cove.Core.Entities;
using Cove.Core.Events;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The text-document path end to end, against a real Cove entity graph and a real directory: the port
/// hydrates a TextDocument and its TextFile onto the Renamer DTOs, the planner renders a name from the
/// entity's own metadata, and the executor moves the file and updates the row. Proves the EF mapping,
/// not just that a fake port returns what it was handed.
/// </summary>
[Collection(SubstDriveScope.CollectionName)]
public sealed class TextRenameIntegrationTests
{
    [Fact]
    public async Task LoadEntity_HydratesTheDocumentAndItsFile()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, textId, _) = await ExecutorTestSeed.SeedTextAsync(
                db, folderPath: "library/docs", basename: "raw scan.pdf", title: "A Manual");

            var studio = new Studio { Name = "Acme Press" };
            db.Set<Studio>().Add(studio);
            var tag = new Tag { Name = "reference" };
            db.Set<Tag>().Add(tag);
            await db.SaveChangesAsync();

            var doc = await db.Set<TextDocument>().FirstAsync(t => t.Id == textId);
            doc.StudioId = studio.Id;
            doc.Date = new DateOnly(2024, 5, 6);
            db.Set<TextTag>().Add(new TextTag { TextDocumentId = textId, TagId = tag.Id });
            await db.SaveChangesAsync();

            var entity = await new CoveRenamerDataPort(db).LoadEntityAsync(RenamerFileKind.Text, textId);

            Assert.NotNull(entity);
            Assert.Equal(RenamerFileKind.Text, entity!.Kind);
            Assert.Equal("A Manual", entity.Title);
            Assert.Equal("Acme Press", entity.StudioName);
            Assert.Equal(studio.Id, entity.StudioId);
            Assert.Equal(new DateOnly(2024, 5, 6), entity.Date);
            Assert.Equal([(tag.Id, "reference")], entity.TagRefs);

            var file = Assert.Single(entity.Files);
            Assert.Equal(RenamerFileKind.Text, file.Kind);
            Assert.Equal("raw scan.pdf", file.Basename);
            Assert.Equal("library/docs", file.ParentFolderPath);
            Assert.Equal("pdf", file.Format);

            // A text file carries none of the media tokens, so each is null and the projector omits it.
            Assert.Null(file.Width);
            Assert.Null(file.Height);
            Assert.Null(file.Duration);
            Assert.Null(file.AudioCodec);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Rename_MovesTheFileOnDisk_UpdatesTheRow_AndPublishesTextUpdated()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, textId, fileId) = await ExecutorTestSeed.SeedTextAsync(
                db, folderPath, "raw scan.pdf", "A Manual");

            string oldFull = Path.Combine(dir.Root, "raw scan.pdf");
            File.WriteAllText(oldFull, "text-bytes");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(port, bus, journal, "run-text", new DiskMover());

            var options = new RenamerOptions { FilenameTemplate = "$title" };

            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Text, textId, options, default);
            var result = await executor.ExecuteAsync(plan, options, default);

            string newFull = Path.Combine(dir.Root, "A Manual.pdf");
            Assert.True(File.Exists(newFull), "renamed file must exist on disk");
            Assert.False(File.Exists(oldFull), "old file must be gone");
            Assert.Equal("text-bytes", File.ReadAllText(newFull));

            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("A Manual.pdf", basename);
            Assert.Equal(folderPath + "/A Manual.pdf", path);

            Assert.Single(result.Renamed);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            var evt = Assert.IsType<EntityEvent>(Assert.Single(bus.Published));
            Assert.Equal(EventType.TextUpdated, evt.Type);
            Assert.Equal("Text", evt.EntityType);
            Assert.Equal(textId, evt.EntityId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task DerivedTitle_IsRecordedOnTheDocument_OnlyWhenItHadNone()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, titlelessId, titlelessFileId) = await ExecutorTestSeed.SeedTextAsync(
                db, "library/docs", "field notes.pdf", title: null!);
            var (_, titledId, titledFileId) = await ExecutorTestSeed.SeedTextAsync(
                db, "library/other", "manual.pdf", "Typed In By Hand");

            var port = new CoveRenamerDataPort(db);
            await port.ApplyAndSaveAsync(
            [
                new RenamerFileMutation(
                    titlelessFileId, "notes renamed.pdf", null, null,
                    new RenamerEntityTitleWrite(RenamerFileKind.Text, titlelessId, "field notes")),
                new RenamerFileMutation(
                    titledFileId, "manual renamed.pdf", null, null,
                    new RenamerEntityTitleWrite(RenamerFileKind.Text, titledId, "manual")),
            ]);

            db.ChangeTracker.Clear();
            var titles = await db.Set<TextDocument>().AsNoTracking()
                .Where(t => t.Id == titlelessId || t.Id == titledId)
                .ToDictionaryAsync(t => t.Id, t => t.Title);

            // Recorded on the document that had none, and never over one a person typed.
            Assert.Equal("field notes", titles[titlelessId]);
            Assert.Equal("Typed In By Hand", titles[titledId]);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Undo_PutsTheFileBack_AndPublishesTextUpdated()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, textId, fileId) = await ExecutorTestSeed.SeedTextAsync(
                db, folderPath, "raw scan.pdf", "A Manual");

            string oldFull = Path.Combine(dir.Root, "raw scan.pdf");
            File.WriteAllText(oldFull, "text-bytes");

            var port = new CoveRenamerDataPort(db);
            var journal = new FakeRevertJournal();
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            await journal.BeginBatchAsync("run-text", "run-text", RenamerFileKind.Text, DateTime.UtcNow);
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Text, textId, options, default);
            var forward = await new RenamerExecutor(
                port, new CapturingEventBus(), journal, "run-text", new DiskMover())
                .ExecuteAsync(plan, options, default);
            Assert.Single(forward.Renamed);

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus, new DiskMover()).RevertAsync(batch!, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            Assert.True(File.Exists(oldFull), "file restored to old path");
            Assert.False(File.Exists(Path.Combine(dir.Root, "A Manual.pdf")), "new path gone after undo");

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw scan.pdf", basename);

            // The undo path keeps its own kind-to-event map, so the kind it publishes is asserted here
            // and not inferred from the forward rename above.
            var evt = Assert.IsType<EntityEvent>(Assert.Single(undoBus.Published));
            Assert.Equal(EventType.TextUpdated, evt.Type);
            Assert.Equal("Text", evt.EntityType);
            Assert.Equal(textId, evt.EntityId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
