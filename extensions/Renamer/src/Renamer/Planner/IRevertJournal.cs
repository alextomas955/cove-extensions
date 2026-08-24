namespace Renamer.Planner;

/// <summary>
/// The undo seam: the ONLY surface between the rename and undo paths and where the revert journal is
/// stored. Faking it (<c>FakeRevertJournal</c>) lets the executor and replayer be exercised with no
/// database at all; the Cove-backed implementation (<c>CoveRevertJournal</c>) reads and writes the
/// extension-owned journal tables.
///
/// TYPE BOUNDARY: this interface speaks ONLY in the Renamer-owned records below — never in an EF
/// entity type and never in a Cove.Core entity type — for the same reason
/// <see cref="IRenamerDataPort"/> does: the production <c>Renamer.csproj</c> takes no runtime
/// dependency on Cove.Core or Cove.Data, so the storage types stay behind the implementation.
///
/// WHAT REMAINS IN THE JOURNAL IS THE WORK LEFT. A row exists exactly while its file still needs
/// restoring: <see cref="DeleteRowAsync"/> removes it as soon as the outcome is known, restorable or
/// not, and the per-batch counters carry the totals the panel reports. There is no per-row status to
/// disagree with the row's own presence, because there is no per-row status.
/// </summary>
public interface IRevertJournal
{
    /// <summary>The hard cap on how many FILES one batch may journal.</summary>
    /// <remarks>
    /// FILES, not entities: the request-level <c>MaxEntityIdsPerRequest</c> bounds a selection to 1000
    /// ENTITIES, and one entity can hold many files, so an entity cap bounds nothing here. A
    /// whole-library run takes no id array and is bounded by nothing at all.
    /// <para>
    /// What it bounds is the undo RESPONSE, not the storage: the table takes a batch of any size, but
    /// <c>/undo</c> answers with one entry per file it could not put back, and a library here reaches
    /// millions of files — so an uncapped batch is a response that grows with the library.
    /// </para>
    /// <para>
    /// It is a refusal, never a trim. A batch past the cap journals NOTHING and the preview says so
    /// before the rename runs, so a rename is either fully reversible or plainly not; trimming instead
    /// would make a partly-journalled rename read exactly like a whole one and the undo after it
    /// quietly partial.
    /// </para>
    /// </remarks>
    const int MaxJournalledFiles = 5000;

    /// <summary>True when a batch of <paramref name="fileCount"/> files is too large to journal.</summary>
    /// <remarks>The one definition of "over cap", read by both the preview and the batch core.</remarks>
    static bool ExceedsCap(int fileCount) => fileCount > MaxJournalledFiles;

    /// <summary>
    /// Opens a batch: records <paramref name="runId"/>, its entity <paramref name="kind"/> and the
    /// moment it opened, with all three counters at zero.
    /// </summary>
    /// <remarks>
    /// <paramref name="nowUtc"/> is passed in rather than read here so the caller's clock is the one
    /// on the record — the timestamp is server time and is read back much later, and elsewhere, to
    /// decide whether the batch is still within its retention window.
    /// <para>
    /// The batch's original count is NOT a parameter: it accrues one per <see cref="AppendAsync"/>,
    /// so a batch that stops early records what it actually journalled rather than what it intended.
    /// </para>
    /// </remarks>
    Task BeginBatchAsync(string runId, RenamerFileKind kind, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Appends one restorable file to <paramref name="row"/>'s batch and counts it on that batch.</summary>
    /// <remarks>
    /// Contract a caller cannot read off the signature: the sequence number is assigned HERE, and
    /// <see cref="RevertRow.Seq"/> on the argument is ignored. It is meaningful only on a row read
    /// back, where it is half of that row's identity.
    /// </remarks>
    Task AppendAsync(RevertRow row, CancellationToken ct = default);

    /// <summary>
    /// Refuses to journal this run: every later <see cref="AppendAsync"/> on this instance does
    /// nothing, and whatever the journal already held is dropped.
    /// </summary>
    /// <remarks>
    /// The over-cap path, and what makes <see cref="MaxJournalledFiles"/> a refusal rather than a trim.
    /// Latching the instance is what makes "an over-cap batch writes no row" structural: one journal is
    /// shared by every parallel worker of a run, and those workers are already in flight.
    /// <para>
    /// Dropping what the journal held is not collateral damage. A run large enough to be refused can
    /// touch any file in the library, so every row still pending may name a file that run has just
    /// moved — offering one of those batches afterwards as "the last rename" would put back the wrong
    /// thing.
    /// </para>
    /// </remarks>
    Task SuppressAsync(CancellationToken ct = default);

    /// <summary>
    /// The batch an undo would act on: the newest batch that still has rows to restore, or — when no
    /// batch has any — the newest batch there is. Null only when nothing was ever journalled.
    /// </summary>
    /// <remarks>
    /// The ONE read that selects a batch. Both the undo endpoint and the panel's summary endpoint call
    /// it and nothing else picks a batch, so the sentence the panel renders and the batch the button
    /// acts on are one value rather than two reads that happen to agree.
    /// <para>
    /// The fallback arm is load-bearing, not a convenience. A spent batch is still what the panel must
    /// describe ("500 files renamed on 3 Aug, 497 restored, 3 could not be"), and that sentence is
    /// derivable only while the aggregate outlives the rows — so when nothing is replayable the newest
    /// aggregate is still the right answer, and a further undo over it is a clean no-op.
    /// </para>
    /// <para>
    /// Reaching FURTHER back than the newest replayable batch is deliberately not offered here: undo
    /// targets one batch, and multi-level undo is a feature that was not chosen.
    /// </para>
    /// </remarks>
    Task<RevertBatchSummary?> ReadUndoTargetAsync(CancellationToken ct = default);

    /// <summary>
    /// At most <paramref name="limit"/> of <paramref name="runId"/>'s rows whose sequence is strictly
    /// below <paramref name="belowSeq"/>, newest-first. Empty when the batch has no such row left.
    /// </summary>
    /// <remarks>
    /// There is deliberately NO read that returns every row of a batch: an undo holds one page at a
    /// time, whatever the cap on a batch is. Pass <see cref="long.MaxValue"/> for the first page and
    /// the lowest sequence the previous page returned for each one after it.
    /// <para>
    /// A KEYSET cursor, never a skip-and-take offset: rows are deleted as they restore, and an offset
    /// over a shrinking table silently skips work. <paramref name="limit"/> bounds one read and never
    /// the run — an undo pages until a page comes back empty, so it restores the whole batch however many
    /// pages that takes.
    /// </para>
    /// <para>
    /// The newest-first order is a correctness requirement, not a presentation one: one run can rename
    /// A→B and then B→C, so reversing in reverse-append order is what frees each slot before the next
    /// row needs it — and that ordering has to hold ACROSS a page boundary, not only within a page.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<RevertRow>> ReadBatchPageAsync(
        string runId, long belowSeq, int limit, CancellationToken ct = default);

    /// <summary>
    /// Retires one row: removes it, and counts it on its batch as restored, or as unrestorable when
    /// <paramref name="unrestorable"/> is set.
    /// </summary>
    /// <remarks>
    /// One call carries both outcomes on purpose. The row goes away either way — a file that can never
    /// be restored must stop being offered as pending work, or its batch never reaches spent and the
    /// panel keeps promising an undo that cannot complete — and the flag only chooses which counter
    /// moves, so the aggregate can still say how the batch ended. That is what makes "what remains in
    /// the table IS the work left" true by construction rather than by a caller's discipline.
    /// <para>
    /// Restoring a row that is already gone is not an error: it does nothing, so a retried undo is safe.
    /// </para>
    /// </remarks>
    Task DeleteRowAsync(string runId, long seq, bool unrestorable, CancellationToken ct = default);

    /// <summary>Drops every batch whose retention window closed before <paramref name="nowUtc"/>, rows and all.</summary>
    /// <remarks>
    /// A batch expires whole. Half a batch surviving would leave a later undo silently partial, with
    /// nothing to say so.
    /// </remarks>
    Task PurgeExpiredAsync(DateTime nowUtc, CancellationToken ct = default);
}

/// <summary>One journalled file: where it was before the rename, and what moved with it.</summary>
/// <param name="RunId">The batch this row belongs to.</param>
/// <param name="Seq">Its position within the batch, assigned on append; the rest of its identity.</param>
/// <param name="EntityId">The PARENT entity id (e.g. the Video id) the forward rename published its event for.</param>
/// <param name="FileId">The renamed physical file row's id. Differs from <paramref name="EntityId"/> in the normal case.</param>
/// <param name="OldPath">The path the file moved FROM (forward-slash).</param>
/// <param name="SidecarsJson">
/// The sidecar and caption moves that rode along with this file, serialized. Empty when none did.
/// Journalled rather than recomputed because which sidecars actually moved is a runtime fact: the
/// forward path renames a caption only for a sidecar whose file really moved on disk, and the caption
/// transform is not invertible from the names alone.
/// </param>
public sealed record RevertRow(
    string RunId,
    long Seq,
    int EntityId,
    int FileId,
    string OldPath,
    string SidecarsJson);

/// <summary>A batch with rows to restore.</summary>
/// <param name="RunId">The batch's run id.</param>
/// <param name="Kind">The run's entity kind — single per run, so it lives on the batch and never on a row.</param>
/// <param name="Rows">
/// Rows still pending, newest-first — ONE PAGE of the batch rather than all of it, since
/// <see cref="IRevertJournal.ReadBatchPageAsync"/> is the only way rows are read. A replay over this
/// record is therefore a replay over part of a run, which is why the caller loops.
/// </param>
public sealed record RevertBatch(string RunId, RenamerFileKind Kind, IReadOnlyList<RevertRow> Rows);

/// <summary>A batch's aggregate: what it started as, and how much of it has been settled.</summary>
/// <param name="RunId">The batch's run id.</param>
/// <param name="Kind">
/// The run's entity kind. SERVER-SIDE ONLY: the undo endpoint needs it for its per-kind write re-gate
/// and for the replayer, and carrying it here is what lets one read answer both. It deliberately does
/// NOT reach the wire summary — a kind on that response would tell a caller holding one kind's read
/// permission which kind was renamed, and would cost the summary endpoint its coarse read gate.
/// </param>
/// <param name="WrittenAtUtcTicks">Server UTC ticks at which the batch opened; what the retention window is measured from.</param>
/// <param name="OriginalCount">How many files the batch journalled. Never decremented.</param>
/// <param name="RestoredCount">How many have been put back.</param>
/// <param name="UnrestorableCount">How many can never be put back.</param>
public readonly record struct RevertBatchSummary(
    string RunId,
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
