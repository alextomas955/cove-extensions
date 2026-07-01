namespace Renamer.Api;

/// <summary>
/// The JSON shape the <c>/undo</c> endpoint returns. Maps directly from
/// <c>UndoReplayer.UndoRunResult</c>: the count of restored entries plus the failed and skipped
/// buckets. A no-batch / empty-log / second-undo call returns <c>Undone:0</c> with empty buckets so
/// the panel can render "No renamer to undo".
/// </summary>
/// <param name="Undone">How many logged entries were restored (disk + DB) and re-published.</param>
/// <param name="Failed">Entries whose reverse move succeeded but the DB save threw (disk rolled back to NEW).</param>
/// <param name="Skipped">Entries skipped because the OLD slot was occupied/locked (never clobbered).</param>
public sealed record UndoResult(int Undone, IReadOnlyList<UndoEntryError> Failed, IReadOnlyList<UndoEntryError> Skipped);

/// <summary>
/// One failed/skipped reverse-replay entry surfaced in <see cref="UndoResult"/> (maps from
/// <c>UndoReplayer.UndoFailure</c>).
/// </summary>
/// <param name="FileId">The physical file row.</param>
/// <param name="OldPath">The original location the reverse move targeted.</param>
/// <param name="NewPath">The renamed location the file currently sits at.</param>
/// <param name="Reason">A human-readable note for the skip/failure.</param>
public sealed record UndoEntryError(int FileId, string OldPath, string NewPath, string Reason);

/// <summary>
/// The JSON shape the <c>/last-batch</c> endpoint returns: a paths-free summary of the most
/// recent batch for the undo panel (maps from <c>RevertLog.RevertBatchSummary</c>). When there is no
/// batch, <see cref="HasBatch"/> is false and the numeric fields are 0/false.
/// </summary>
/// <param name="HasBatch">True iff a batch exists in the log.</param>
/// <param name="Count">The batch's data-row count.</param>
/// <param name="WrittenAtUtcTicks">The server-written UTC ticks when the batch opened (0 for none/legacy).</param>
/// <param name="Consumed">True iff the batch has already been undone.</param>
public sealed record LastBatchSummary(bool HasBatch, int Count, long WrittenAtUtcTicks, bool Consumed);
