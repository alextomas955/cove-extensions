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
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class RevertJournalCapTests
{
    private static readonly DateTime Opened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ARefusedRun_JournalsNothing_AndDropsWhatWasStillPending()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        // An earlier, still-replayable batch. A run large enough to be refused can move any file in the
        // library, so offering this one afterwards as "the last rename" would put back the wrong thing.
        using (var earlier = new CoveRevertJournal(db))
        {
            await earlier.BeginBatchAsync("earlier", RenamerFileKind.Video, Opened);
            await earlier.AppendAsync(new RevertRow("earlier", Seq: 0, 11, 21, "/media/old/a.mkv", ""));
        }

        using var refused = new CoveRevertJournal(db);
        await refused.SuppressAsync();

        // The refused run's workers are already in flight when the decision is taken, so every append
        // they go on to make has to be a no-op — otherwise a partial journal forms behind the refusal.
        for (int i = 1; i <= 10; i++)
        {
            await refused.AppendAsync(
                new RevertRow("refused", Seq: 0, 100 + i, 200 + i, $"/media/old/{i}.mkv", ""));
        }

        Assert.Null(await refused.ReadUndoTargetAsync());
        Assert.Null(await JournalPageReader.ReadWholeUndoTargetAsync(refused));
        Assert.Empty(await refused.ReadBatchPageAsync("refused", long.MaxValue, limit: 100));
        Assert.Empty(await refused.ReadBatchPageAsync("earlier", long.MaxValue, limit: 100));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(IRevertJournal.MaxJournalledFiles - 1)]
    [InlineData(IRevertJournal.MaxJournalledFiles)]
    public async Task AtOrUnderTheCap_TheBatchIsOpened(int actingFiles)
    {
        var journal = new FakeRevertJournal();

        await RenamerFixture.Create().OpenOrSuppressBatchAsync(
            journal, "run", RenamerFileKind.Video, actingFiles, Opened, default);

        await journal.AppendAsync(new RevertRow("run", Seq: 0, 11, 21, "/media/old/a.mkv", ""));

        var target = await journal.ReadUndoTargetAsync();
        Assert.NotNull(target);
        Assert.Equal(RenamerFileKind.Video, target!.Value.Kind);
        Assert.Single(journal.PendingRows);
    }

    [Theory]
    [InlineData(IRevertJournal.MaxJournalledFiles + 1)]
    [InlineData(IRevertJournal.MaxJournalledFiles * 2)]
    public async Task PastTheCap_NoBatchIsOpened_AndLaterAppendsAreDropped(int actingFiles)
    {
        var journal = new FakeRevertJournal();

        await RenamerFixture.Create().OpenOrSuppressBatchAsync(
            journal, "run", RenamerFileKind.Video, actingFiles, Opened, default);

        // Workers already in flight when the decision was taken still call AppendAsync.
        await journal.AppendAsync(new RevertRow("run", Seq: 0, 11, 21, "/media/old/a.mkv", ""));

        Assert.Null(await journal.ReadUndoTargetAsync());
        Assert.Empty(journal.PendingRows);
    }
}
