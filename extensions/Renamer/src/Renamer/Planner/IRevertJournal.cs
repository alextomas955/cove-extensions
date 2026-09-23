namespace Renamer.Planner;

/// <summary>The undo seam between the rename and undo paths and where the revert journal is stored.</summary>
/// <remarks>
/// Speaks only in the Renamer-owned records below, never an EF or Cove.Core type, for the reason
/// <see cref="IRenamerDataPort"/> gives. A row exists exactly while its file still needs restoring,
/// so there is no per-row status to disagree with the row's own presence; the per-batch counters
/// carry the totals the panel reports.
/// </remarks>
public interface IRevertJournal
{
    /// <summary>The batch cursor an operation's first <see cref="ReadNextBatchAsync"/> call passes.</summary>
    /// <remarks>
    /// No real batch's timestamp reaches <see cref="long.MaxValue"/>, so the first call admits every
    /// batch the operation has.
    /// </remarks>
    const long FirstBatchTicks = long.MaxValue;

    const string FirstBatchRunId = "~";

    /// <summary>
    /// Opens a batch, recording its run id, the operation it belongs to, its entity kind and the
    /// moment it opened, with all three counters at zero.
    /// </summary>
    /// <remarks>
    /// <paramref name="nowUtc"/> is the caller's clock, read back much later to decide whether the
    /// batch is still within its retention window. The original count accrues one per
    /// <see cref="AppendAsync"/>, so a batch that stops early records what it journalled. A
    /// whole-library rename opens one batch per kind, which is why an undo acts on the operation.
    /// </remarks>
    Task BeginBatchAsync(
        string runId, string operationId, RenamerFileKind kind, DateTime nowUtc,
        CancellationToken ct = default);

    /// <summary>Appends one restorable file to <paramref name="row"/>'s batch and counts it on that batch.</summary>
    /// <remarks>
    /// The sequence number is assigned here; <see cref="RevertRow.Seq"/> on the argument is ignored.
    /// </remarks>
    Task AppendAsync(RevertRow row, CancellationToken ct = default);

    /// <summary>
    /// The operation an undo would act on: the newest operation that still has rows to restore, or,
    /// when none has any, the newest operation there is. Null only when nothing was ever journalled.
    /// </summary>
    /// <remarks>
    /// Both the undo endpoint and the panel's summary endpoint call this, so the sentence the panel
    /// renders and the work the button does come from one value. The fallback to a spent operation
    /// is what lets the panel still describe it, and a further undo over it is a clean no-op.
    /// </remarks>
    Task<RevertOperationSummary?> ReadUndoTargetAsync(CancellationToken ct = default);

    /// <summary>
    /// The next batch of <paramref name="operationId"/> that still holds rows, strictly older than
    /// the (<paramref name="beforeOpenedAtTicks"/>, <paramref name="beforeRunId"/>) cursor. Null when
    /// the operation has no such batch left.
    /// </summary>
    /// <remarks>
    /// A keyset cursor, not a set of batches already seen: a row that failed for a reason the world
    /// can clear stays in the journal, so without a cursor the loop would offer its batch forever.
    /// Newest-first for the reason <see cref="ReadBatchPageAsync"/> gives. The first call passes
    /// <see cref="FirstBatchTicks"/> and <see cref="FirstBatchRunId"/>.
    /// </remarks>
    Task<RevertBatchSummary?> ReadNextBatchAsync(
        string operationId, long beforeOpenedAtTicks, string beforeRunId, CancellationToken ct = default);

    /// <summary>The distinct entity kinds <paramref name="operationId"/>'s batches name, at most four.</summary>
    /// <remarks>
    /// Lets the undo endpoint re-gate on every kind the operation touched before it restores any.
    /// Bounded by the renamable kinds, so this is the one journal read with no page.
    /// </remarks>
    Task<IReadOnlyList<RenamerFileKind>> ReadOperationKindsAsync(
        string operationId, CancellationToken ct = default);

    /// <summary>
    /// At most <paramref name="limit"/> of <paramref name="runId"/>'s rows whose sequence is strictly
    /// below <paramref name="belowSeq"/>, newest-first. Empty when the batch has no such row left.
    /// </summary>
    /// <remarks>
    /// No read returns a whole batch, so an undo holds one page at a time: pass
    /// <see cref="long.MaxValue"/> first, then the lowest sequence the previous page returned. The
    /// cursor is a keyset because rows are deleted as they restore, and an offset over a shrinking
    /// table silently skips work. Newest-first is a correctness requirement that must hold across a
    /// page boundary: one run can rename A to B and then B to C, and reversing in reverse-append
    /// order is what frees each slot before the next row needs it.
    /// </remarks>
    Task<IReadOnlyList<RevertRow>> ReadBatchPageAsync(
        string runId, long belowSeq, int limit, CancellationToken ct = default);

    /// <summary>
    /// Retires one row: removes it, and counts it on its batch as restored, or as unrestorable when
    /// <paramref name="unrestorable"/> is set.
    /// </summary>
    /// <remarks>
    /// The row goes away either way: a file that can never be restored must stop being offered as
    /// pending work, or its batch never reaches spent and the panel keeps promising an undo that
    /// cannot complete. The flag only chooses which counter moves. Retiring a row that is already
    /// gone does nothing, so a retried undo is safe.
    /// </remarks>
    Task DeleteRowAsync(string runId, long seq, bool unrestorable, CancellationToken ct = default);

    /// <summary>Drops every batch whose retention window closed before <paramref name="nowUtc"/>, rows and all.</summary>
    /// <remarks>
    /// A batch expires whole. Half a batch surviving would leave a later undo silently partial, with
    /// nothing to say so.
    /// </remarks>
    Task PurgeExpiredAsync(DateTime nowUtc, CancellationToken ct = default);
}

/// <summary>One journalled file move, replayed backwards to restore the file.</summary>
/// <remarks>
/// <c>EntityId</c> is the parent entity the forward rename published its event for, not
/// <c>FileId</c>, the renamed physical file row. <c>OldPath</c> is forward-slash form.
/// <c>SidecarsJson</c> is journalled rather than recomputed because which sidecars actually moved is
/// a runtime fact, and the caption transform is not invertible from the names alone.
/// </remarks>
public sealed record RevertRow(
    string RunId,
    long Seq,
    int EntityId,
    int FileId,
    string OldPath,
    string SidecarsJson);

/// <summary>A batch with rows to restore.</summary>
/// <remarks>
/// <c>Kind</c> is single per run, so it lives on the batch and never on a row. <c>Rows</c> is one
/// page, newest-first, which is why a replay over this record covers only part of a run.
/// </remarks>
public sealed record RevertBatch(string RunId, RenamerFileKind Kind, IReadOnlyList<RevertRow> Rows);

/// <summary>One user action's aggregate, summed over every batch it opened.</summary>
/// <remarks>
/// <c>OpenedAtUtcTicks</c> is the earliest of its batches' open timestamps, so it is the moment the
/// user clicked. The purge still measures retention from each batch's own timestamp, so a run
/// spanning the cutoff loses its earliest batches and a later undo restores only part of it.
/// </remarks>
public readonly record struct RevertOperationSummary(
    string OperationId,
    long OpenedAtUtcTicks,
    int OriginalCount,
    int RestoredCount,
    int UnrestorableCount)
{
    /// <summary>How many files the operation still has to restore.</summary>
    /// <remarks>Derived, never stored: three numbers that must sum correctly can disagree.</remarks>
    public int Remaining => OriginalCount - RestoredCount - UnrestorableCount;
}

/// <summary>One batch of an operation, as the undo loop walks them.</summary>
/// <remarks>
/// <c>WrittenAtUtcTicks</c> and <c>RunId</c> are the walk's cursor. <c>Kind</c> never reaches the wire,
/// because it would tell a caller holding one kind's read permission which kind was renamed.
/// </remarks>
public readonly record struct RevertBatchSummary(string RunId, RenamerFileKind Kind, long WrittenAtUtcTicks);
