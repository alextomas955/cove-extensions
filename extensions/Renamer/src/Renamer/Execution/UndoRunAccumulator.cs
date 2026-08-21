using Renamer.Contracts;

namespace Renamer.Execution;

/// <summary>
/// Folds one page of an undo run at a time into the totals and the bounded samples the
/// <see cref="UndoResult"/> carries, so the response a whole-library undo writes is bounded by
/// <see cref="MaxSampleEntries"/> rather than by the batch.
/// </summary>
/// <remarks>
/// Pure by construction: it takes nothing but <see cref="UndoReplayer.UndoRunResult"/> records and
/// touches no database, no host store and no filesystem. That is what lets its suite sit in
/// <c>Renamer.Tests/Contracts/</c> and run on the continuous-integration leg that has no cove checkout.
/// <para>
/// The fold exists because the handler reads a batch a page at a time: a page's own result is already
/// bounded by the page, and accumulating those results verbatim would rebuild exactly the
/// library-sized value the paging removed. What accumulates here instead is four integers and three
/// short lists.
/// </para>
/// </remarks>
public sealed class UndoRunAccumulator
{
    // How many entries of each problem bucket the response describes. A DESCRIPTION cap, never a limit
    // on the work: nothing here is consulted by a restore or a retirement, so an undo of any size
    // restores and retires every row it reaches, and the totals beside each sample state what actually
    // happened.
    //
    // 20 because of who reads a sample. The panel names exactly ONE reason — the first — so anything
    // above one is for a log reader or a later surface asking "do these share a cause?", and twenty
    // distinct entries answer that while an enumeration of the run would not. It also keeps the weight
    // fixed: an entry carries two absolute paths and a reason, so three full samples are tens of
    // kilobytes whether the batch held 20 files or the 7,459 a real library does.
    public const int MaxSampleEntries = 20;

    private readonly List<UndoEntryError> _failedSample = [];
    private readonly List<UndoEntryError> _skippedSample = [];
    private readonly List<UndoEntryWarning> _warningSample = [];

    private int _undone;
    private int _failedCount;
    private int _skippedCount;
    private int _warningCount;

    /// <summary>Folds one page's replay result into the running totals and samples.</summary>
    /// <remarks>
    /// Every bucket's total moves by the whole page; a sample grows only while it is under the cap, so
    /// the entries it keeps are the ones the run hit first. A run that stops for the same cause on
    /// every row therefore shows that cause rather than whichever row a last page happened to hold.
    /// </remarks>
    public void Add(UndoReplayer.UndoRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        _undone += run.Undone;

        _failedCount += run.Failed.Count;
        SampleStops(_failedSample, run.Failed);

        _skippedCount += run.Skipped.Count;
        SampleStops(_skippedSample, run.Skipped);

        _warningCount += run.Warnings.Count;
        for (int i = 0; i < run.Warnings.Count && _warningSample.Count < MaxSampleEntries; i++)
        {
            _warningSample.Add(new UndoEntryWarning(run.Warnings[i].FileId, run.Warnings[i].Detail));
        }
    }

    /// <summary>The response body for everything folded so far.</summary>
    public UndoResult ToResult() => new(
        _undone,
        _failedCount,
        [.. _failedSample],
        _skippedCount,
        [.. _skippedSample],
        _warningCount,
        [.. _warningSample]);

    // The row identity (RunId, Seq) the replayer reports alongside each stop deliberately does not
    // travel: the endpoint retires rows from the replayer's own records, and a caller has no use for a
    // journal sequence it cannot address.
    private static void SampleStops(
        List<UndoEntryError> sample, IReadOnlyList<UndoReplayer.UndoFailure> stopped)
    {
        for (int i = 0; i < stopped.Count && sample.Count < MaxSampleEntries; i++)
        {
            sample.Add(new UndoEntryError(
                stopped[i].FileId, stopped[i].OldPath, stopped[i].NewPath, stopped[i].Reason));
        }
    }
}
