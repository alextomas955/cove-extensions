using Cove.Plugins;
using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

/// <summary>
/// The one-shot legacy journal migration: what an installation still carries under the two stored keys
/// becomes one batch in the journal table, and both keys go — including when the value cannot be read.
/// </summary>
/// <remarks>
/// Every legacy fixture here is HAND-WRITTEN rather than produced by the code that reads it. A fixture
/// generated from the parser under test agrees with that parser forever, whatever either of them says;
/// a transcribed one fails when the format claim is wrong. Assertions are on the OUTCOME — the keys are
/// gone, the batch is readable from the table — never on the migration having been called.
/// </remarks>
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class JournalBlobMigrationTests
{
    /// <summary>A hand-written header: run <c>R1</c>, opened 3 Aug 2026 10:00 UTC, video, still replayable.</summary>
    private static readonly DateTime HeaderOpened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    private static string Header(string runId = "R1", string kind = "Video", string status = "open") =>
        $"#batch{'|'}{runId}{'|'}{HeaderOpened.Ticks}{'|'}{kind}{'|'}{status}";

    private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AHeaderedJournal_BecomesOneBatchStampedWithTheHeadersOwnTimestamp()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var store = await StampedStoreAsync(string.Join("\n", Header(), "7|70|/lib/a.mkv", "8|80|/lib/b.mkv"));
        var journal = new CoveRevertJournal(db);

        int moved = await JournalBlobMigration.RunAsync(store, journal, Now);

        Assert.Equal(2, moved);

        var summary = await journal.ReadUndoTargetAsync();
        Assert.NotNull(summary);
        Assert.Equal("R1", summary.Value.RunId);

        // Stamped with the header's own moment, NOT with Now: a pending undo must keep its real age
        // against the retention window, or a batch that should already have expired is silently extended.
        Assert.Equal(HeaderOpened.Ticks, summary.Value.WrittenAtUtcTicks);
        Assert.NotEqual(Now.Ticks, summary.Value.WrittenAtUtcTicks);
        Assert.Equal(2, summary.Value.OriginalCount);

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
        Assert.NotNull(batch);
        Assert.Equal(RenamerFileKind.Video, batch.Kind);
        Assert.Equal([(8, 80, "/lib/b.mkv"), (7, 70, "/lib/a.mkv")],
            batch.Rows.Select(r => (r.EntityId, r.FileId, r.OldPath)));

        await AssertBothKeysGoneAsync(store);
    }

    [Fact]
    public async Task AHeaderlessJournal_MigratesThroughTheTolerantLegacyPath()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        // The pre-header shape: fileId|old|new rows and no batch line at all.
        var store = await StampedStoreAsync("70|/lib/a.mkv|/lib/A.mkv\n80|/lib/b.mkv|/lib/B.mkv");
        var journal = new CoveRevertJournal(db);

        int moved = await JournalBlobMigration.RunAsync(store, journal, Now);

        Assert.Equal(2, moved);

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
        Assert.NotNull(batch);
        Assert.Equal(RenamerFileKind.Video, batch.Kind);

        // The legacy path has no parent entity to record, so each row's entity id IS its file id.
        Assert.All(batch.Rows, r => Assert.Equal(r.FileId, r.EntityId));
        Assert.Equal([80, 70], batch.Rows.Select(r => r.FileId));

        // No header means no timestamp to inherit. Treating an unknown age as EXPIRED would delete a
        // pending undo on the next batch open with nothing to say so, which is the outcome this phase
        // exists to make impossible — so an unknown age gets the full window instead.
        var summary = await journal.ReadUndoTargetAsync();
        Assert.NotNull(summary);
        Assert.Equal(Now.Ticks, summary.Value.WrittenAtUtcTicks);

        await AssertBothKeysGoneAsync(store);
    }

    [Fact]
    public async Task ASecondRun_DoesNothing_BecauseTheSourceKeysAreAlreadyGone()
    {
        // Deleting the source keys IS the idempotency marker. A separate "already migrated" flag would
        // be a second marker, and a second marker is something that can disagree with the first.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var store = await StampedStoreAsync(string.Join("\n", Header(), "7|70|/lib/a.mkv"));
        var journal = new CoveRevertJournal(db);

        Assert.Equal(1, await JournalBlobMigration.RunAsync(store, journal, Now));
        Assert.Equal(0, await JournalBlobMigration.RunAsync(store, journal, Now));

        var summary = await journal.ReadUndoTargetAsync();
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Value.OriginalCount);
    }

    [Fact]
    public async Task AStoredValueLargerThanOneChunk_ArrivesInFull()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        int rows = (JournalBlobMigration.LinesPerChunk * 2) + 7;
        var lines = new List<string> { Header() };
        for (int i = 0; i < rows; i++)
        {
            lines.Add($"{1000 + i}|{5000 + i}|/lib/f-{i}.mkv");
        }

        var store = await StampedStoreAsync(string.Join("\n", lines));
        var journal = new CoveRevertJournal(db);

        Assert.Equal(rows, await JournalBlobMigration.RunAsync(store, journal, Now));

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
        Assert.NotNull(batch);
        Assert.Equal(rows, batch.Rows.Count);

        // Every row arrived exactly once, with its own sequence number — a chunk boundary that dropped
        // or repeated a line would show as a short count or a repeated key.
        Assert.Equal(rows, batch.Rows.Select(r => r.FileId).Distinct().Count());
        Assert.Equal(rows, batch.Rows.Select(r => r.Seq).Distinct().Count());
        Assert.Contains(batch.Rows, r => r.FileId == 5000);
        Assert.Contains(batch.Rows, r => r.FileId == 5000 + rows - 1);
    }

    [Fact]
    public async Task AnUnparseableStoredValue_StillClearsBothKeys()
    {
        // Leaving them is the failure state, not a safe fallback: an oversized leftover is what made an
        // extension's settings page fail, survive reinstall and need SQL to clear.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var store = await StampedStoreAsync("[written under a shape this code does not read]");
        var journal = new CoveRevertJournal(db);

        Assert.Equal(0, await JournalBlobMigration.RunAsync(store, journal, Now));

        // Nothing was invented from an unreadable value — no empty batch was opened either.
        Assert.Null(await journal.ReadUndoTargetAsync());
        await AssertBothKeysGoneAsync(store);
    }

    [Fact]
    public async Task AJournalWhoseOnlyBatchWasAlreadySpent_MigratesNothing_AndBothKeysGo()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var store = await StampedStoreAsync(
            string.Join("\n", Header(status: "consumed"), "7|70|/lib/a.mkv"));
        var journal = new CoveRevertJournal(db);

        Assert.Equal(0, await JournalBlobMigration.RunAsync(store, journal, Now));

        Assert.Null(await journal.ReadUndoTargetAsync());
        await AssertBothKeysGoneAsync(store);
    }

    [Fact]
    public async Task LoadingTheExtension_RunsTheMigration_AndTheKeysStopBeingServed()
    {
        // The real entry path, not the migration in isolation: the host applies this extension's schema
        // migration BEFORE InitializeAsync on every load path, which is what makes this placement safe.
        await using var library = await LibraryDatabase.CreateAsync();

        var store = new FakeStore();
        await store.SetAsync(RevertLog.SchemaKey, RevertLog.CurrentSchema);
        await store.SetAsync(RevertLog.Key, string.Join("\n", Header(), "7|70|/lib/a.mkv"));

        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(library.BuildProvider());

        // The endpoint that broke on an oversized leftover serves every key an extension owns, so the
        // proof that it stops serving these is simply that they are gone.
        Assert.Empty(await store.GetAllAsync());

        await using var db = library.NewContext();
        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(new CoveRevertJournal(db));
        Assert.NotNull(batch);
        Assert.Equal("R1", batch.RunId);
        Assert.Equal("/lib/a.mkv", Assert.Single(batch.Rows).OldPath);
    }

    private static async Task<FakeStore> StampedStoreAsync(string blob)
    {
        var store = new FakeStore();
        await store.SetAsync(RevertLog.SchemaKey, RevertLog.CurrentSchema);
        await store.SetAsync(RevertLog.Key, blob);
        return store;
    }

    private static async Task AssertBothKeysGoneAsync(FakeStore store)
    {
        Assert.Null(await store.GetAsync(RevertLog.Key));
        Assert.Null(await store.GetAsync(RevertLog.SchemaKey));
    }
}
