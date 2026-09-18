using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

/// <summary>
/// The over-cap refusal against a real <c>CoveContext</c>: a refused run journals no row of its own,
/// and takes whatever was still pending with it.
/// </summary>
/// <remarks>
/// What the cap keeps bounded is the undo RESPONSE — <c>/undo</c> answers with one entry per file it
/// could not put back — so the claim under test is that a refused run leaves nothing for a later undo
/// to page over at all. Driven through the real EF implementation rather than the fake, because that
/// is a property of the storage and a fake reimplementing it would only agree with itself.
/// </remarks>
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class RevertJournalCapTests
{
    private static readonly DateTime Opened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ARefusedOperation_JournalsNothing_AndTakesItsOwnEarlierKindsWithIt()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        // The same click's first kind, already journalled. A run large enough to be refused can move a
        // file that kind renamed, so offering its batch afterwards would put back the wrong thing.
        using (var firstKind = new CoveRevertJournal(db))
        {
            await firstKind.BeginBatchAsync("run-video", "op", RenamerFileKind.Video, Opened);
            await firstKind.AppendAsync(new RevertRow("run-video", Seq: 0, 11, 21, "/media/old/a.mkv", ""));
        }

        using var refused = new CoveRevertJournal(db);
        await refused.SuppressAsync("op");

        // The refused run's workers are already in flight when the decision is taken, so every append
        // they go on to make has to be a no-op — otherwise a partial journal forms behind the refusal.
        for (int i = 1; i <= 10; i++)
        {
            await refused.AppendAsync(
                new RevertRow("run-image", Seq: 0, 100 + i, 200 + i, $"/media/old/{i}.mkv", ""));
        }

        Assert.Null(await refused.ReadUndoTargetAsync());
        Assert.Null(await JournalPageReader.ReadWholeUndoTargetAsync(refused));
        Assert.Empty(await refused.ReadBatchPageAsync("run-image", long.MaxValue, limit: 100));
        Assert.Empty(await refused.ReadBatchPageAsync("run-video", long.MaxValue, limit: 100));
    }

    [Fact]
    public async Task ARefusedOperation_LeavesAnotherOperationsJournalAlone()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        using (var other = new CoveRevertJournal(db))
        {
            await other.BeginBatchAsync("run-other", "op-other", RenamerFileKind.Video, Opened);
            await other.AppendAsync(new RevertRow("run-other", Seq: 0, 11, 21, "/media/old/a.mkv", ""));
        }

        using var refused = new CoveRevertJournal(db);
        await refused.SuppressAsync("op-refused");

        var target = await refused.ReadUndoTargetAsync();
        Assert.NotNull(target);
        Assert.Equal("op-other", target!.Value.OperationId);
        Assert.Single(await refused.ReadBatchPageAsync("run-other", long.MaxValue, limit: 100));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(IRevertJournal.MaxJournalledFiles - 1)]
    [InlineData(IRevertJournal.MaxJournalledFiles)]
    public async Task AtOrUnderTheCap_TheBatchIsOpened(int actingFiles)
    {
        var journal = new FakeRevertJournal();

        await RenamerFixture.Create().OpenOrSuppressBatchAsync(
            journal, "run", "op", RenamerFileKind.Video, actingFiles, Opened, default);

        await journal.AppendAsync(new RevertRow("run", Seq: 0, 11, 21, "/media/old/a.mkv", ""));

        var target = await journal.ReadUndoTargetAsync();
        Assert.NotNull(target);
        Assert.Equal("op", target!.Value.OperationId);
        Assert.Single(journal.PendingRows);
    }

    [Theory]
    [InlineData(IRevertJournal.MaxJournalledFiles + 1)]
    [InlineData(IRevertJournal.MaxJournalledFiles * 2)]
    public async Task PastTheCap_NoBatchIsOpened_AndLaterAppendsAreDropped(int actingFiles)
    {
        var journal = new FakeRevertJournal();

        await RenamerFixture.Create().OpenOrSuppressBatchAsync(
            journal, "run", "op", RenamerFileKind.Video, actingFiles, Opened, default);

        // Workers already in flight when the decision was taken still call AppendAsync.
        await journal.AppendAsync(new RevertRow("run", Seq: 0, 11, 21, "/media/old/a.mkv", ""));

        Assert.Null(await journal.ReadUndoTargetAsync());
        Assert.Empty(journal.PendingRows);
    }
}
