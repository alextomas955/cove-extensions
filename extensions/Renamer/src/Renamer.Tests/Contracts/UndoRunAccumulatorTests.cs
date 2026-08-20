using Renamer.Execution;

namespace Renamer.Tests.Contracts;

/// <summary>
/// Pins what an undo run reports about itself: what the response says about a run larger than anyone
/// wants described, and which reasons for a row stopping retire it for good. No store, no database
/// context, no filesystem — neither subject touches one, which is why this suite needs no setup, no
/// doubles and no running service.
/// </summary>
/// <remarks>
/// PLACEMENT IS LOAD-BEARING, and this file is deliberately NOT under <c>Execution/</c>. The cove-absent
/// continuous-integration leg removes cove-dependent sources from those folders FILE BY FILE, so whether
/// a pure suite placed beside the code it covers keeps running there depends on a <c>Compile Remove</c>
/// entry nobody adds deliberately for a test that needs none. <c>Contracts/</c> is covered by no such
/// entry at all, which is the guarantee. Do not "tidy" this file next to <c>UndoRunAccumulator.cs</c>.
/// <para>
/// Every fixture below is a hand-built page result, and every classification is a hand-written table
/// entry. Nothing here is produced by folding one accumulator into another or read back out of the
/// classifier, which would only prove each agrees with itself; the expected totals and classifications
/// are written out as literals so a fold that lost a page, or a reason that silently changed meaning,
/// fails here instead of agreeing with its own arithmetic.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class UndoRunAccumulatorTests
{
    private static UndoReplayer.UndoRunResult Page(
        int undone = 0,
        IReadOnlyList<UndoReplayer.UndoFailure>? failed = null,
        IReadOnlyList<UndoReplayer.UndoFailure>? skipped = null,
        IReadOnlyList<UndoReplayer.UndoWarning>? warnings = null) =>
        new(undone, failed ?? [], skipped ?? [], [], warnings ?? []);

    /// <summary>One stopped row whose file id also serves as its sequence, so an entry is identifiable.</summary>
    private static UndoReplayer.UndoFailure Stop(int fileId, string reason = "locked") =>
        new("run-a", fileId, fileId, $"/old/{fileId}.mkv", $"/new/{fileId}.mkv", reason,
            UndoStopReason.OriginalLocationOccupied);

    private static IReadOnlyList<UndoReplayer.UndoFailure> Stops(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => Stop(i))];

    [Fact]
    public void FoldingNothing_IsZeroTotalsAndEmptySamples()
    {
        // The no-batch answer the endpoint returns before it reads anything, produced by the same type
        // that produces every other answer — so the two cannot drift into different shapes.
        var result = new UndoRunAccumulator().ToResult();

        Assert.Equal(0, result.Undone);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.WarningCount);
        Assert.Empty(result.FailedSample);
        Assert.Empty(result.SkippedSample);
        Assert.Empty(result.WarningSample);
    }

    [Fact]
    public void FoldingSeveralPages_TotalsAreTheSumWhateverThePageBoundariesWere()
    {
        // The page size is a read granularity, so the same six stopped rows must answer identically
        // whether they arrived as two pages or as three. A fold that reset per page passes the first
        // arm and fails the second.
        var twoPages = new UndoRunAccumulator();
        twoPages.Add(Page(undone: 2, failed: Stops(1, 2), skipped: Stops(11, 1)));
        twoPages.Add(Page(undone: 3, failed: Stops(3, 1), skipped: Stops(12, 2)));

        var threePages = new UndoRunAccumulator();
        threePages.Add(Page(undone: 1, failed: Stops(1, 1)));
        threePages.Add(Page(undone: 3, failed: Stops(2, 2), skipped: Stops(11, 2)));
        threePages.Add(Page(undone: 1, skipped: Stops(13, 1)));

        foreach (var result in new[] { twoPages.ToResult(), threePages.ToResult() })
        {
            Assert.Equal(5, result.Undone);
            Assert.Equal(3, result.FailedCount);
            Assert.Equal(3, result.SkippedCount);
        }
    }

    [Fact]
    public void ABucketUnderTheCap_IsSampledInFull()
    {
        var accumulator = new UndoRunAccumulator();
        accumulator.Add(Page(failed: Stops(1, 3), skipped: Stops(11, 1)));

        var result = accumulator.ToResult();

        Assert.Equal(3, result.FailedCount);
        Assert.Equal(3, result.FailedSample.Count);
        Assert.Equal(1, result.SkippedCount);
        Assert.Single(result.SkippedSample);
        // The projection carries the pair of paths and the reason a reader needs; the row identity the
        // endpoint retires on deliberately does not travel.
        Assert.Equal("/old/1.mkv", result.FailedSample[0].OldPath);
        Assert.Equal("/new/1.mkv", result.FailedSample[0].NewPath);
        Assert.Equal("locked", result.FailedSample[0].Reason);
    }

    [Fact]
    public void ABucketOverTheCap_ReportsTheRealTotal_AndSamplesExactlyTheCap()
    {
        // The claim the panel's honesty rests on. Written as one page deliberately larger than the cap
        // and one run split across pages, because a cap applied per page rather than per run would let
        // the second arm through.
        int overCap = UndoRunAccumulator.MaxSampleEntries + 7;

        var onePage = new UndoRunAccumulator();
        onePage.Add(Page(failed: Stops(1, overCap)));

        var manyPages = new UndoRunAccumulator();
        for (int i = 0; i < overCap; i++)
        {
            manyPages.Add(Page(skipped: Stops(i + 1, 1), warnings: [new UndoReplayer.UndoWarning(i + 1, "sidecar stayed")]));
        }

        var single = onePage.ToResult();
        Assert.Equal(overCap, single.FailedCount);
        Assert.Equal(UndoRunAccumulator.MaxSampleEntries, single.FailedSample.Count);

        var paged = manyPages.ToResult();
        Assert.Equal(overCap, paged.SkippedCount);
        Assert.Equal(UndoRunAccumulator.MaxSampleEntries, paged.SkippedSample.Count);
        Assert.Equal(overCap, paged.WarningCount);
        Assert.Equal(UndoRunAccumulator.MaxSampleEntries, paged.WarningSample.Count);
    }

    [Fact]
    public void TheSampleHoldsTheFirstEntriesEncountered_NotAnArbitrarySubset()
    {
        // Which entries survive matters: a run stops for the same handful of causes, so the entries a
        // reader is shown must be the ones the run hit first rather than the ones a later page
        // happened to overwrite. Asserted on identity, not on the count.
        int overCap = UndoRunAccumulator.MaxSampleEntries + 5;
        var accumulator = new UndoRunAccumulator();
        for (int i = 1; i <= overCap; i++)
        {
            accumulator.Add(Page(failed: [Stop(i, $"reason {i}")]));
        }

        var sample = accumulator.ToResult().FailedSample;

        Assert.Equal(
            [.. Enumerable.Range(1, UndoRunAccumulator.MaxSampleEntries)],
            sample.Select(e => e.FileId));
        Assert.Equal("reason 1", sample[0].Reason);
    }

    [Fact]
    public void TheRestoredTotal_IsTheSumAcrossPages_AndIsNeverCapped()
    {
        // The number that must never be bounded by a presentation cap: it is what the undo actually
        // did. Driven well past the cap so a cap wrongly applied to it fails here rather than in a
        // user's library.
        int pages = UndoRunAccumulator.MaxSampleEntries * 3;
        var accumulator = new UndoRunAccumulator();
        for (int i = 0; i < pages; i++)
        {
            accumulator.Add(Page(undone: 7));
        }

        Assert.Equal(pages * 7, accumulator.ToResult().Undone);
    }

    [Fact]
    public void TheWarningChannel_IsCountedAndSampledLikeTheProblemBuckets()
    {
        // A stranded companion is in neither problem bucket and is the only record the panel has that a
        // restore was partial, so it gets a total of its own rather than riding on a problem count.
        var accumulator = new UndoRunAccumulator();
        accumulator.Add(Page(undone: 2, warnings:
        [
            new UndoReplayer.UndoWarning(1, "companion 'a.srt' stayed behind: target occupied"),
            new UndoReplayer.UndoWarning(2, "companion 'b.srt' stayed behind: target occupied"),
        ]));

        var result = accumulator.ToResult();

        Assert.Equal(2, result.Undone);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(2, result.WarningCount);
        Assert.Equal(
            ["companion 'a.srt' stayed behind: target occupied", "companion 'b.srt' stayed behind: target occupied"],
            result.WarningSample.Select(w => w.Detail));
    }

    [Fact]
    public void TheCap_IsAtLeastOne_SoAProblemAlwaysCarriesAReason()
    {
        // The panel names one reason and reads it out of a sample. A cap of zero would leave every
        // problem count stated with nothing to explain it, which is the one value of this constant
        // that would break a caller rather than only narrow it.
        Assert.True(UndoRunAccumulator.MaxSampleEntries >= 1);
    }

    /// <summary>
    /// Every stop reason and the classification it was deliberately given: true is terminal — the row is
    /// retired as unrestorable — and false stays pending to be retried. Transcribed by hand from the
    /// decision, never generated from the enum.
    /// </summary>
    /// <remarks>
    /// The pairing with <see cref="EveryMemberOfTheTypeAppearsInTheTable_SoAnUnclassifiedOneFailsRatherThanDefaults"/>
    /// is the point: a member added later without a deliberate entry here fails the suite instead of
    /// quietly inheriting whatever the classifier happens to return for it.
    /// </remarks>
    public static TheoryData<UndoStopReason, bool> EveryStopReason => new()
    {
        { UndoStopReason.UnexpectedError, false },
        { UndoStopReason.FileNoLongerInLibrary, true },
        { UndoStopReason.RestoreTargetRejectedByAllowlist, false },
        { UndoStopReason.OriginalDirectoryUnavailable, false },
        { UndoStopReason.OriginalLocationOccupied, false },
        { UndoStopReason.ReverseMoveLockedOrTargetExists, false },
        { UndoStopReason.ReverseMovePermissionDenied, false },
        { UndoStopReason.ReverseMoveVerifyFailed, false },
        { UndoStopReason.ReverseMoveCancelled, false },
        { UndoStopReason.RestoredPathMismatch, false },
        { UndoStopReason.DatabaseSaveFailed, false },
    };

    [Theory]
    [MemberData(nameof(EveryStopReason))]
    public void EachStopReason_ClassifiesAsTheTableSays(UndoStopReason reason, bool terminal) =>
        Assert.Equal(terminal, UndoTerminalClassifier.IsTerminal(reason));

    [Fact]
    public void EveryMemberOfTheTypeAppearsInTheTable_SoAnUnclassifiedOneFailsRatherThanDefaults()
    {
        var classified = EveryStopReason.Select(row => (UndoStopReason)row[0]!).ToHashSet();

        Assert.Equal(Enum.GetValues<UndoStopReason>().ToHashSet(), classified);
    }

    [Fact]
    public void ExactlyOneReasonIsTerminal_AndItIsTheFileLeavingTheLibrary()
    {
        // The asymmetry IS the safety property: a reason wrongly called terminal retires the row that
        // holds the user's only route back to their file, while a reason wrongly called retryable costs
        // one row that the retention window sweeps anyway.
        var terminal = Enum.GetValues<UndoStopReason>().Where(UndoTerminalClassifier.IsTerminal);

        Assert.Equal([UndoStopReason.FileNoLongerInLibrary], terminal);
    }

    [Fact]
    public void TheDefaultValue_IsRetryable_SoAnUnsetReasonNeverRetiresARow()
    {
        // A reason nobody assigned must not be the one that deletes a row for good.
        Assert.False(UndoTerminalClassifier.IsTerminal(default));
    }
}
