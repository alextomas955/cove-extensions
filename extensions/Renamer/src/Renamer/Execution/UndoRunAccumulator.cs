using Renamer.Contracts;

namespace Renamer.Execution;

// Folds one page of an undo run at a time into the totals and the bounded samples UndoResult carries,
// so a whole-library undo's response is bounded by MaxSampleEntries and not by the batch.
//
// The handler reads a batch a page at a time. A page's result is bounded by the page, and accumulating
// those results verbatim would rebuild the library-sized value the paging removed. What accumulates
// here is four integers and three short lists.
public sealed class UndoRunAccumulator
{
    // How many entries of each problem bucket the response describes. A cap on the description and not
    // on the work: nothing here is consulted by a restore or a retirement, so an undo of any size
    // restores and retires every row it reaches, and the totals beside each sample state what happened.
    //
    // The panel names one reason, so the rest of a sample is for a log reader asking whether the
    // entries share a cause. It also keeps the response weight fixed whatever the batch size, since an
    // entry carries two absolute paths and a reason.
    public const int MaxSampleEntries = 20;

    private readonly List<UndoEntryError> _failedSample = [];
    private readonly List<UndoEntryError> _skippedSample = [];
    private readonly List<UndoEntryWarning> _warningSample = [];

    private int _undone;
    private int _failedCount;
    private int _skippedCount;
    private int _warningCount;

    // Every bucket's total moves by the whole page, while a sample grows only while it is under the
    // cap, so the entries it keeps are the ones the run hit first. A run that stops for the same cause
    // on every row shows that cause and not whichever row the last page held.
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

    public UndoResult ToResult() => new(
        _undone,
        _failedCount,
        [.. _failedSample],
        _skippedCount,
        [.. _skippedSample],
        _warningCount,
        [.. _warningSample]);

    // The row identity (RunId, Seq) the replayer reports beside each stop does not travel: the endpoint
    // retires rows from the replayer's own records, and a caller cannot address a journal sequence.
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
