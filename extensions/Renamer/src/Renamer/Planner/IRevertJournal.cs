namespace Renamer.Planner;

/// <summary>The undo seam between the rename and undo paths and where the revert journal is stored.</summary>
/// <remarks>
/// This interface speaks only in the Renamer-owned records below, never in an EF or Cove.Core entity
/// type, for the reason <see cref="IRenamerDataPort"/> gives: the production <c>Renamer.csproj</c>
/// takes no runtime dependency on Cove.Core or Cove.Data. A row exists exactly while its file still
/// needs restoring, so what remains in the journal is the work left; <see cref="DeleteRowAsync"/>
/// removes a row as soon as its outcome is known, restorable or not, and the per-batch counters
/// carry the totals the panel reports. There is no per-row status to disagree with the row's own
/// presence.
/// </remarks>
public interface IRevertJournal
{
    /// <summary>The batch cursor an operation's first <see cref="ReadNextBatchAsync"/> call passes.</summary>
    /// <remarks>
    /// No real batch's timestamp reaches <see cref="long.MaxValue"/>, so the first call admits every
    /// batch the operation has. The run id half of the cursor is consulted only on an exact timestamp
    /// tie, which this value never produces.
    /// </remarks>
    const long FirstBatchTicks = long.MaxValue;

    const string FirstBatchRunId = "~";

    /// <summary>
    /// Opens a batch, recording its run id, the operation it belongs to, its entity kind and the
    /// moment it opened, with all three counters at zero.
    /// </summary>
    /// <remarks>
    /// <paramref name="nowUtc"/> is passed in so the caller's clock is the one on the record; the
    /// timestamp is server time and is read back much later, and elsewhere, to decide whether the
    /// batch is still within its retention window. The original count is not a parameter: it accrues
    /// one per <see cref="AppendAsync"/>, so a batch that stops early records what it journalled. One
    /// user action is one operation, and a whole-library rename opens one batch per kind, so an undo
    /// acts on the operation.
    /// </remarks>
    Task BeginBatchAsync(
        string runId, string operationId, RenamerFileKind kind, DateTime nowUtc,
        CancellationToken ct = default);

    /// <summary>Appends one restorable file to <paramref name="row"/>'s batch and counts it on that batch.</summary>
    /// <remarks>
    /// The sequence number is assigned here and <see cref="RevertRow.Seq"/> on the argument is
    /// ignored. It is meaningful only on a row read back, where it is half of that row's identity.
    /// </remarks>
    Task AppendAsync(RevertRow row, CancellationToken ct = default);

    /// <summary>
    /// The operation an undo would act on: the newest operation that still has rows to restore, or,
    /// when none has any, the newest operation there is. Null only when nothing was ever journalled.
    /// </summary>
    /// <remarks>
    /// The one read that selects what undo acts on, called by both the undo endpoint and the panel's
    /// summary endpoint, so the sentence the panel renders and the work the button does are one
    /// value. It answers over the operation, because one click can open a batch per media kind. The
    /// fallback arm is load-bearing: a spent operation is still what the panel must describe, and
    /// that sentence is derivable only while the aggregate outlives the rows, so a further undo over
    /// it is a clean no-op. Reaching further back than the newest replayable operation is not
    /// offered, since undo targets one operation.
    /// </remarks>
    Task<RevertOperationSummary?> ReadUndoTargetAsync(CancellationToken ct = default);

    /// <summary>
    /// The next batch of <paramref name="operationId"/> that still holds rows, strictly older than
    /// the (<paramref name="beforeOpenedAtTicks"/>, <paramref name="beforeRunId"/>) cursor. Null when
    /// the operation has no such batch left.
    /// </summary>
    /// <remarks>
    /// A keyset over the operation's batches, and a cursor, not a set of batches already seen: a row
    /// that failed for a reason the world can clear stays in the journal, so its batch still has rows
    /// when the undo comes back round to it, and without a cursor the loop would offer that batch
    /// forever. Newest-first for the same reason rows are, since one operation can rename A to B in
    /// one kind's batch and B to C in another's. The first call passes <see cref="FirstBatchTicks"/>
    /// and <see cref="FirstBatchRunId"/>.
    /// </remarks>
    Task<RevertBatchSummary?> ReadNextBatchAsync(
        string operationId, long beforeOpenedAtTicks, string beforeRunId, CancellationToken ct = default);

    /// <summary>The distinct entity kinds <paramref name="operationId"/>'s batches name, at most four.</summary>
    /// <remarks>
    /// Lets the undo endpoint re-gate on every kind the operation touched before it restores any of
    /// them. Bounded by the renamable kinds, so this is the one journal read with no page.
    /// </remarks>
    Task<IReadOnlyList<RenamerFileKind>> ReadOperationKindsAsync(
        string operationId, CancellationToken ct = default);

    /// <summary>
    /// At most <paramref name="limit"/> of <paramref name="runId"/>'s rows whose sequence is strictly
    /// below <paramref name="belowSeq"/>, newest-first. Empty when the batch has no such row left.
    /// </summary>
    /// <remarks>
    /// There is deliberately no read that returns every row of a batch, so an undo holds one page at
    /// a time; pass <see cref="long.MaxValue"/> for the first page and the lowest sequence the
    /// previous page returned for each one after it. The cursor is a keyset, because rows are deleted
    /// as they restore and an offset over a shrinking table silently skips work.
    /// <paramref name="limit"/> bounds one read and never the run, which pages until a page comes
    /// back empty. The newest-first order is a correctness requirement that has to hold across a page
    /// boundary: one run can rename A to B and then B to C, and reversing in reverse-append order is
    /// what frees each slot before the next row needs it.
    /// </remarks>
    Task<IReadOnlyList<RevertRow>> ReadBatchPageAsync(
        string runId, long belowSeq, int limit, CancellationToken ct = default);

    /// <summary>
    /// Retires one row: removes it, and counts it on its batch as restored, or as unrestorable when
    /// <paramref name="unrestorable"/> is set.
    /// </summary>
    /// <remarks>
    /// The row goes away either way, because a file that can never be restored must stop being
    /// offered as pending work or its batch never reaches spent and the panel keeps promising an undo
    /// that cannot complete; the flag only chooses which counter moves, so the aggregate can still
    /// say how the batch ended. Retiring a row that is already gone does nothing, so a retried undo
    /// is safe.
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
/// <c>Seq</c> is the row's position within the batch, assigned on append, and the rest of its
/// identity. <c>EntityId</c> is the parent entity the forward rename published its event for, which
/// differs from <c>FileId</c>, the renamed physical file row. <c>OldPath</c> is the path the file
/// moved from, forward-slash form. <c>SidecarsJson</c> serializes the sidecar and caption moves that
/// rode along, empty when none did; it is journalled because which sidecars actually moved is a
/// runtime fact, and the caption transform is not invertible from the names alone.
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
/// <c>Kind</c> is the run's entity kind, single per run, so it lives on the batch and never on a
/// row. <c>Rows</c> holds the rows still pending, newest-first, and is one page of the batch, since
/// <see cref="IRevertJournal.ReadBatchPageAsync"/> is the only way rows are read. A replay over this
/// record covers part of a run, which is why the caller loops.
/// </remarks>
public sealed record RevertBatch(string RunId, RenamerFileKind Kind, IReadOnlyList<RevertRow> Rows);

/// <summary>One user action's aggregate, summed over every batch it opened.</summary>
/// <param name="OperationId">The operation's id, which is what an undo acts on.</param>
/// <param name="OpenedAtUtcTicks">
/// The earliest of its batches' open timestamps, which is the moment the user clicked rather than the
/// moment its last kind started. The purge still measures the retention window from each batch's own
/// timestamp, so a run spanning the cutoff loses its earliest batches while later ones remain and a
/// later undo restores only part of it.
/// </param>
/// <param name="OriginalCount">How many files the operation journalled, over all its batches.</param>
/// <param name="RestoredCount">How many have been put back.</param>
/// <param name="UnrestorableCount">How many can never be put back.</param>
public readonly record struct RevertOperationSummary(
    string OperationId,
    long OpenedAtUtcTicks,
    int OriginalCount,
    int RestoredCount,
    int UnrestorableCount)
{
    /// <summary>How many files the operation still has to restore.</summary>
    /// <remarks>Derived, never stored, for the reason <see cref="RevertBatchSummary.Remaining"/> gives.</remarks>
    public int Remaining => OriginalCount - RestoredCount - UnrestorableCount;
}

/// <summary>A batch's aggregate: what it started as, and how much of it has been settled.</summary>
/// <remarks>
/// <c>OperationId</c> is the user action this batch belongs to, resolved on read, so a batch written
/// before the column existed reads as an operation of one. <c>WrittenAtUtcTicks</c> is server UTC at
/// which the batch opened and is what the retention window is measured from. <c>OriginalCount</c> is
/// how many files the batch journalled and is never decremented; the other two counts say how many
/// have been put back and how many never can be.
/// <para>
/// <c>Kind</c> is the run's entity kind, which the undo endpoint needs for its per-kind write re-gate
/// and for the replayer, so one read answers both. It does not reach the wire summary: a kind on that
/// response would tell a caller holding one kind's read permission which kind was renamed, and would
/// cost the summary endpoint its coarse read gate.
/// </para>
/// </remarks>
public readonly record struct RevertBatchSummary(
    string RunId,
    string OperationId,
    RenamerFileKind Kind,
    long WrittenAtUtcTicks,
    int OriginalCount,
    int RestoredCount,
    int UnrestorableCount)
{
    /// <summary>How many files the batch still has to restore.</summary>
    /// <remarks>
    /// Derived, never stored: three numbers that must sum correctly are three numbers that can
    /// disagree, and the one a stale writer would corrupt is the one the button acts on.
    /// </remarks>
    public int Remaining => OriginalCount - RestoredCount - UnrestorableCount;
}
