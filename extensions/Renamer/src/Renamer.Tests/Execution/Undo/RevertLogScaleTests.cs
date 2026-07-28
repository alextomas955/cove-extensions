using Renamer.Execution;
using Renamer.Planner;

namespace Renamer.Tests.Execution.Undo;

/// <summary>
/// What the undo journal stores must be bounded by its SHAPE: the blob's character length stays under a
/// ceiling computed from <see cref="RevertLog.MaxJournalledFiles"/> and the row form, at ten files and
/// again at the cap itself; a batch over the cap stores nothing. The same claim
/// <c>ScanAggregateScaleTests</c> makes for the other writer that grew one line per file. The ceiling is
/// derived from the production constant, so raising the cap moves it rather than invalidating the proof.
/// </summary>
[Trait("Tier", "L0")]
public sealed class RevertLogScaleTests
{
    private const int SmallFixture = 10;

    /// <summary>
    /// The character budget one row may occupy: two ten-digit ids, their separators, the newline, and a
    /// path allowed the whole Windows MAX_PATH budget — generous on every term.
    /// </summary>
    private const int MaxPathChars = 260;
    private const int CharsPerRow = (2 * 10) + 3 + MaxPathChars;

    /// <summary>The batch header's own budget: a tag, a 32-char run id, a tick count, a kind and a marker.</summary>
    private const int HeaderChars = 128;

    private static int Ceiling() => HeaderChars + (RevertLog.MaxJournalledFiles * CharsPerRow);

    /// <summary>A path at the per-row allowance, so the ceiling is measured against a full row.</summary>
    private static string LongPath(int index)
    {
        string suffix = $"/file-{index}.mkv";
        return "/" + new string('d', MaxPathChars - suffix.Length - 1) + suffix;
    }

    private static async Task<string> JournalAsync(int files)
    {
        var store = new FakeStore();
        var log = new RevertLog(store);

        await log.BeginBatchAsync(Guid.NewGuid().ToString("N"), RenamerFileKind.Video);
        for (int i = 0; i < files; i++)
        {
            await log.AppendAsync(entityId: 1_000_000 + i, fileId: 2_000_000 + i, oldPath: LongPath(i));
        }

        return (await store.GetAsync(RevertLog.Key))!;
    }

    [Theory]
    [InlineData(SmallFixture)]
    [InlineData(RevertLog.MaxJournalledFiles)]
    public async Task StoredBlob_StaysUnderTheDerivedCeiling(int files)
    {
        string blob = await JournalAsync(files);

        Assert.True(blob.Length < Ceiling(),
            $"{files}-file journal stored {blob.Length} characters, over the derived ceiling of {Ceiling()}");
    }

    [Fact]
    public async Task StoredBlob_HoldsEveryRowOfAFullBatch()
    {
        string blob = await JournalAsync(RevertLog.MaxJournalledFiles);

        // The ceiling above bounds a COMPLETE journal, not one trimmed to meet it.
        Assert.Equal(1 + RevertLog.MaxJournalledFiles, blob.Split('\n').Length);
    }

    [Fact]
    public async Task BatchOverTheCap_JournalsNothing_AndDropsAnyEarlierBatch()
    {
        var store = new FakeStore();
        var log = new RevertLog(store);

        // An earlier, still-undoable batch. Offering it as "the last rename" after the over-cap rename
        // has moved files would restore the wrong thing — the refusal has to take it with it.
        await log.BeginBatchAsync("EARLIER", RenamerFileKind.Video);
        await log.AppendAsync(entityId: 1, fileId: 11, oldPath: "/lib/a.mkv");

        Assert.True(RevertLog.ExceedsCap(RevertLog.MaxJournalledFiles + 1));
        await log.SuppressAsync();

        // Every append the batch goes on to make is a no-op, so no partial journal can form.
        for (int i = 0; i < SmallFixture; i++)
        {
            await log.AppendAsync(entityId: 100 + i, fileId: 200 + i, oldPath: LongPath(i));
        }

        Assert.Empty((await store.GetAsync(RevertLog.Key))!);
        Assert.Empty(log.Rows);
        Assert.Null(await log.ReadLastOpenBatchAsync());
        Assert.Null(await log.ReadLastBatchSummaryAsync());
    }

    [Fact]
    public void Cap_AdmitsExactlyTheCapAndRefusesOneMore()
    {
        Assert.False(RevertLog.ExceedsCap(RevertLog.MaxJournalledFiles));
        Assert.True(RevertLog.ExceedsCap(RevertLog.MaxJournalledFiles + 1));
    }
}
