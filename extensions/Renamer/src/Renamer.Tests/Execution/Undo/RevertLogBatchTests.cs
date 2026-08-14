using Renamer.Execution;
using Renamer.Planner;

namespace Renamer.Tests.Execution.Undo;

/// <summary>
/// The tolerant parsers that read a legacy stored journal — the only part of that type still reached,
/// and the only reader the one-shot migration uses. Locating a batch finds the LAST still-replayable
/// header and the line range holding its rows; parsing that range yields <c>entityId|fileId|old</c>
/// rows in append order, each entity id distinct from its file id. A flat pre-header blob is one
/// implicit Video batch with EntityId = FileId. A batch already spent leaves nothing to locate.
/// Malformed and short lines are skipped, never thrown.
/// </summary>
/// <remarks>
/// Every fixture is hand-written. There is no writer left to build one with, and that is the point:
/// a fixture produced by the code under test agrees with it forever, whatever the real stored format is.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class RevertLogBatchTests
{
    private static (RevertLog.LegacyBatch Batch, List<RevertLog.RevertEntry> Rows) Read(string blob)
    {
        var lines = RevertLog.SplitLines(blob);
        var located = RevertLog.LocateLastOpenBatch(lines);
        Assert.NotNull(located);
        var batch = located.Value;
        return (batch, RevertLog.ParseRows(lines, batch.RowStart, batch.RowEnd, batch.Headerless));
    }

    [Fact]
    public void TwoRuns_TheLocatedBatchIsTheSecond_WithItsRowsOnly_InAppendOrder()
    {
        var (batch, rows) = Read(string.Join("\n",
            "#batch|R1|638000000000000000|Video|open",
            "7|70|media/a.mkv",
            "8|80|media/b.mkv",
            "#batch|R2|638000000000000001|Video|open",
            "9|90|media/c.mkv",
            "10|100|media/d.mkv"));

        Assert.Equal("R2", batch.RunId);
        Assert.Equal(638000000000000001L, batch.WrittenAtUtcTicks);
        Assert.Equal(RenamerFileKind.Video, batch.Kind);
        Assert.False(batch.Headerless);

        // Append order, not reversed: the reversal undo needs is the journal port's, and it is minted
        // from the sequence number the table owns rather than from a position in a parsed list.
        Assert.Equal([9, 10], rows.Select(e => e.EntityId));
        Assert.Equal([90, 100], rows.Select(e => e.FileId));

        // Each entry's EntityId is the PARENT entity (9), never its fileId (90). The earlier run's rows
        // are absent entirely, which is what makes this the located batch rather than the whole blob.
        Assert.All(rows, e => Assert.NotEqual(e.EntityId, e.FileId));
    }

    [Fact]
    public void TwoEntityBatch_RoundTrips_BothDistinctEntityIds_InAppendOrder()
    {
        var (_, rows) = Read(string.Join("\n",
            "#batch|R1|638000000000000000|Image|open",
            "7|70|media/a.mkv",
            "8|80|media/b.mkv"));

        // Append order, not reversed: the reversal undo needs is the journal port's, and it is minted
        // from the sequence number the table owns rather than from a position in a parsed list.
        Assert.Equal([7, 8], rows.Select(e => e.EntityId));
        Assert.All(rows, e => Assert.NotEqual(e.EntityId, e.FileId));
    }

    [Fact]
    public void TheHeadersKindIsCarried_AndAnUnknownKindFallsBackToVideo()
    {
        Assert.Equal(RenamerFileKind.Image,
            Read("#batch|R1|1|Image|open\n7|70|a").Batch.Kind);

        Assert.Equal(RenamerFileKind.Video,
            Read("#batch|R1|1|Sculpture|open\n7|70|a").Batch.Kind);
    }

    [Fact]
    public void FlatLegacyBlob_ReadsAsOneVideoBatch_EntityIdEqualsFileId()
    {
        // An old flat blob: fileId|old|new rows, NO #batch headers.
        var (batch, rows) = Read("70|media/a.mkv|media/A.mkv\n80|media/b.mkv|media/B.mkv");

        Assert.True(batch.Headerless);
        Assert.Equal("", batch.RunId);
        Assert.Equal(0, batch.WrittenAtUtcTicks);
        Assert.Equal(RenamerFileKind.Video, batch.Kind);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, e => Assert.Equal(e.FileId, e.EntityId));
        Assert.Equal([70, 80], rows.Select(e => e.FileId));
        Assert.Equal("media/a.mkv", rows[0].OldPath);
    }

    [Fact]
    public void ABlobWhoseOnlyBatchIsSpent_LocatesNothing()
    {
        var lines = RevertLog.SplitLines("#batch|R1|1|Video|consumed\n7|70|media/a.mkv");

        Assert.Null(RevertLog.LocateLastOpenBatch(lines));
    }

    [Fact]
    public void MalformedLines_AreTolerated_NeverThrow()
    {
        // A short header (missing fields), a non-integer row and a short row, mixed with one valid
        // header and one valid row. Nothing throws; only the valid row parses.
        var (batch, rows) = Read(string.Join("\n",
            "#batch|RBAD",                       // header with too few fields → skipped
            "#batch|R1|123456789|Video|open",    // valid open header
            "notanint|x|y|z",                    // non-int entityId → skipped
            "7|short",                           // too few fields → skipped
            "7|70|media/a.mkv|media/A.mkv"));    // valid row, with a trailing field the parser ignores

        Assert.Equal("R1", batch.RunId);
        var only = Assert.Single(rows);
        Assert.Equal(7, only.EntityId);
        Assert.Equal(70, only.FileId);
        Assert.Equal("media/a.mkv", only.OldPath);
    }

    [Fact]
    public void ARangeCanBeParsedInSlices_AndTheSlicesRebuildTheWholeBatch()
    {
        // The shape the migration relies on: the located range is parsed a window at a time, so no list
        // the size of the stored value ever exists beside it.
        var lines = RevertLog.SplitLines(string.Join("\n",
            ["#batch|R1|1|Video|open", .. Enumerable.Range(0, 9).Select(i => $"{i}|{100 + i}|f-{i}.mkv")]));
        var batch = RevertLog.LocateLastOpenBatch(lines)!.Value;

        var sliced = new List<RevertLog.RevertEntry>();
        for (int start = batch.RowStart; start < batch.RowEnd; start += 4)
        {
            sliced.AddRange(RevertLog.ParseRows(lines, start, Math.Min(start + 4, batch.RowEnd), batch.Headerless));
        }

        Assert.Equal(
            RevertLog.ParseRows(lines, batch.RowStart, batch.RowEnd, batch.Headerless),
            sliced);
    }
}
