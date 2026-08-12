using System.Text.Json;
using Cove.Extensions.Shared;
using Renamer.Planner;

namespace Renamer.Contracts;

/// <summary>
/// The Cove-facing wire projection of a <see cref="RenamerPlanItem"/>: the fields the preview
/// response serializes, decoupled from the plan/domain model so the planner and executor can evolve
/// <see cref="RenamerPlanItem"/> without breaking the wire: every UI response is a projection type,
/// never a live domain or EF type. The property order and names are the wire contract
/// the UI reads; <see cref="From"/> is the sole mapping from the domain item.
/// </summary>
/// <param name="FileId">The Cove <c>BaseFileEntity.Id</c> this item plans.</param>
/// <param name="OldFullPath">The file's current full path (forward-slash).</param>
/// <param name="NewFullPath">The intended new full path (forward-slash), or the old path for a NoOp/skip.</param>
/// <param name="Status">The planner's classification.</param>
/// <param name="NewBasename">The resolved new basename (name + ext).</param>
/// <param name="TargetFolderPath">The resolved absolute target folder path (forward-slash).</param>
/// <param name="Reason">Human-readable reason for a skip/no-op (null for a plain renamer/move).</param>
/// <param name="Suffixed">UI badge signal: true iff the collision suffix loop ran.</param>
/// <param name="Sanitized">UI badge signal: true iff the engine cleaned the rendered name.</param>
/// <param name="InFlightPathOverflow">
/// UI badge signal: true iff this item crosses volumes and its in-flight copy would overrun the path
/// budget, so the move the user is approving cannot complete even though the planned path fits.
/// </param>
/// <param name="ResolvedDestinationRoot">The routed destination-root template, or null for a source-confine/in-place item.</param>
/// <param name="MatchedRule">
/// The resolver's matched-rule label for preview/log (<c>"Studio:42(direct)"</c>, <c>"InPlace"</c>, …),
/// or <c>""</c> for an item the resolver never routed — a skip or a no-op.
/// </param>
/// <param name="TargetVolume">
/// The destination volume of the resolved absolute target, or <c>""</c> when the item is not a routed
/// move.
/// </param>
public sealed record PreviewItemView(
    int FileId,
    string OldFullPath,
    string NewFullPath,
    RenamerStatus Status,
    string NewBasename,
    string TargetFolderPath,
    string? Reason,
    bool Suffixed,
    bool Sanitized,
    bool InFlightPathOverflow,
    string? ResolvedDestinationRoot,
    string MatchedRule,
    string TargetVolume)
{
    /// <summary>Projects a planned <paramref name="item"/> onto its wire shape.</summary>
    /// <param name="item">The planned item to project.</param>
    /// <param name="inFlightPathOverflow">
    /// Passed in rather than derived here, because the answer needs the volume classification and the
    /// configured path budget, and this projection is a pure field mapping with access to neither. The
    /// caller computes it from <c>BatchPreview.InFlightPathOverflows</c>, which is the same comparison the
    /// aggregate's count reads, so the count and the flags cannot disagree.
    /// </param>
    public static PreviewItemView From(RenamerPlanItem item, bool inFlightPathOverflow) => new(
        item.FileId,
        item.OldFullPath,
        item.NewFullPath,
        item.Status,
        item.NewBasename,
        item.TargetFolderPath,
        item.Reason,
        item.Suffixed,
        item.Sanitized,
        inFlightPathOverflow,
        item.ResolvedDestinationRoot,
        item.MatchedRule,
        item.TargetVolume);
}

/// <summary>
/// The <c>/preview</c> response body: the per-item plan PLUS the whole-batch blast-radius
/// summary. Carries batch-level aggregates (count, same/cross split, per-volume bytes, the scaled
/// confirm level) without losing the per-item array contract the UI matches on (<c>status === "renamer"</c>).
/// Both halves ride the same camelCase + string-enum serializer, so <see cref="Items"/> keeps its exact
/// wire shape and <see cref="PreviewSummary.ConfirmLevel"/> serializes as "light"/"standard"/"heavy" —
/// lowercase-first, because the converter camel-cases the enum NAME just as it does a property name.
/// </summary>
/// <param name="Items">One <see cref="PreviewItemView"/> per physical file of the selection, in plan order.</param>
/// <param name="Summary">The whole-batch blast radius computed over the acting items.</param>
public sealed record PreviewResponse(
    IReadOnlyList<PreviewItemView> Items,
    PreviewSummary Summary);

/// <summary>
/// One built-in sample's live-preview result: the synthetic "before" filename, the engine-rendered
/// "after" name+ext and folder, and the advisory flags the UI surfaces. Computed by the real
/// <see cref="Engine.TemplateEngine"/> so the preview matches a real renamer exactly.
/// </summary>
/// <param name="SampleLabel">Human label of the sample shape: <c>"Video"</c> / <c>"Image"</c> / <c>"Audio"</c>.</param>
/// <param name="OldName">The synthetic original filename shown as the "before" (UI diff old side).</param>
/// <param name="NewName">The engine-rendered filename including its extension (the "after").</param>
/// <param name="Folder">The engine-rendered relative folder path (may be empty = no folder move).</param>
/// <param name="Flags">
/// Stable string codes the UI maps to copy: <c>"empty"</c>, <c>"sanitized"</c>, <c>"length-reduced"</c>,
/// <c>"gating-skip"</c>. Order is not significant.
/// </param>
/// <param name="DroppedFields">
/// When <see cref="Flags"/> contains <c>"length-reduced"</c>, the <see cref="Options.RenamerOptions.DropOrder"/>
/// fields actually dropped (reported by the engine), so the UI can show "dropped: {fields}". Empty otherwise.
/// </param>
public sealed record PreviewSampleResult(
    string SampleLabel,
    string OldName,
    string NewName,
    string Folder,
    string[] Flags,
    string[] DroppedFields);

/// <summary>
/// The JSON shape the <c>/undo</c> endpoint returns. Folded from the per-page
/// <c>UndoReplayer.UndoRunResult</c>s by <c>Execution.UndoRunAccumulator</c>: the count of restored
/// entries, and for each of the three problem channels a TOTAL and a bounded SAMPLE. A no-batch /
/// empty-log / second-undo call returns <c>Undone:0</c> with zero totals so the panel can render
/// "No renamer to undo".
/// </summary>
/// <remarks>
/// Every channel is a <c>…Count</c> paired with a <c>…Sample</c>, and the pairing is the point: a batch
/// reaches library size, so an entry per problem was a payload proportional to the library on a
/// response that crosses to a browser. The counts are what anyone states; the samples are only how a
/// reason is named. Reading <c>Sample.Count</c> as a total is then visibly the wrong member rather than
/// a plausible one — which is the only structural guarantee a flat wire record can offer, so the
/// behavioural half lives in the panel's own suite.
/// <para>
/// There is deliberately no "truncated" flag. A total beside its sample already says whether the sample
/// is complete, and a fourth number that must agree with the other two is a number that can disagree.
/// </para>
/// </remarks>
/// <param name="Undone">
/// How many logged entries were restored (disk + DB) and re-published. Never capped — it is what the
/// undo actually did, and the sample caps below bound only what the response describes.
/// </param>
/// <param name="FailedCount">How many entries' reverse move succeeded but whose DB save threw (disk rolled back to NEW).</param>
/// <param name="FailedSample">
/// At most <c>UndoRunAccumulator.MaxSampleEntries</c> of those, in the order the run hit them.
/// </param>
/// <param name="SkippedCount">How many entries were skipped because the OLD slot was occupied/locked (never clobbered).</param>
/// <param name="SkippedSample">
/// At most <c>UndoRunAccumulator.MaxSampleEntries</c> of those, in the order the run hit them.
/// </param>
/// <param name="WarningCount">
/// How many entries WERE restored but left a companion behind — a sidecar or caption whose own reverse
/// move stopped. They are in no problem bucket, so without this channel the only record of a partial
/// restore is the host log, which the panel cannot read.
/// </param>
/// <param name="WarningSample">
/// At most <c>UndoRunAccumulator.MaxSampleEntries</c> of those, in the order the run hit them.
/// </param>
public sealed record UndoResult(
    int Undone,
    int FailedCount,
    IReadOnlyList<UndoEntryError> FailedSample,
    int SkippedCount,
    IReadOnlyList<UndoEntryError> SkippedSample,
    int WarningCount,
    IReadOnlyList<UndoEntryWarning> WarningSample);

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
/// One restored-but-incomplete entry surfaced in <see cref="UndoResult"/> (maps from
/// <c>UndoReplayer.UndoWarning</c>). Carries no path pair, because the media file DID return to its
/// original path — only a companion did not, and the detail names which.
/// </summary>
/// <param name="FileId">The physical file row that was restored.</param>
/// <param name="Detail">A human-readable note naming the companion that stayed behind and why.</param>
public sealed record UndoEntryWarning(int FileId, string Detail);

/// <summary>
/// The JSON shape the <c>/last-batch</c> endpoint returns: a paths-free summary of the most
/// recent batch for the undo panel (maps from <c>RevertBatchSummary</c>). When there is no
/// batch, <see cref="HasBatch"/> is false and the numeric fields are 0/false.
/// </summary>
/// <remarks>
/// Counts only — never a path, never a kind, never a per-file collection. That is what lets the
/// endpoint keep its coarse any-renamer-read gate, and it is also what keeps the response O(1) in a
/// library of unbounded size.
/// </remarks>
/// <param name="HasBatch">True iff a batch has ever been journalled.</param>
/// <param name="Count">How many files the batch journalled — never decremented as they are restored.</param>
/// <param name="RemainingCount">
/// How many of those files are still waiting to be put back, so the panel can state the work left
/// after a partial undo rather than only what the batch started as. Derived server-side from the
/// batch aggregate, so it cannot disagree with <see cref="Count"/>:
/// remaining + restored + unrestorable == original.
/// </param>
/// <param name="UnrestorableCount">How many of those files can never be put back, and were retired on that counter.</param>
/// <param name="WrittenAtUtcTicks">The server-written UTC ticks when the batch opened (0 for none).</param>
/// <param name="Consumed">True iff the batch has nothing left to restore.</param>
public sealed record LastBatchSummary(
    bool HasBatch,
    int Count,
    int RemainingCount,
    int UnrestorableCount,
    long WrittenAtUtcTicks,
    bool Consumed);

/// <summary>
/// The <c>202</c> body every enqueueing endpoint returns (<c>/renamer</c>, <c>/scan-library</c>,
/// <c>/renamer-library</c>): the host job id the caller polls. One record serves all three because the
/// three shapes are identical, and a named one serves them because an anonymous shape describes no
/// schema at all.
/// </summary>
/// <param name="JobId">The id <c>IJobService.Enqueue</c> minted, as passed to the host's job API.</param>
public sealed record JobEnqueued(string JobId);

/// <summary>The wire-serialization home for Renamer's Cove-facing response DTOs.</summary>
public static class PreviewContracts
{
    /// <summary>
    /// Response-serialization options for the preview/scan/picker endpoints: camelCase to match the
    /// host's wire convention (and the UI's field names) plus a string-enum converter so
    /// <c>status</c> serializes as the string the UI matches (<c>"renamer"</c>/<c>"noOp"</c>/…, the
    /// enum name camel-cased). The host's default minimal-API serializer emits NUMERIC enums, which the
    /// frontend's <c>buildConfirmSummary</c> would read as a non-renamer — so the extension serializes
    /// here.
    /// <para>
    /// This instance has a second job: the wire-document emit copies its members into the emitting
    /// host's JSON options, so the schema's property casing and enum spelling are a consequence of the
    /// options the responses actually ride rather than a second declaration that could drift from them.
    /// </para>
    /// </summary>
    public static readonly JsonSerializerOptions PreviewResponseJsonOptions = CoveJsonOptions.WebWithEnumStrings();
}
