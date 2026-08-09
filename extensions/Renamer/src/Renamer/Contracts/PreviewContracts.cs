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
/// <param name="ResolvedDestinationRoot">The routed destination-root template, or null for a source-confine/in-place item.</param>
/// <param name="MatchedRule">The resolver's matched-rule label for preview/log.</param>
/// <param name="TargetVolume">The destination volume of the resolved absolute target.</param>
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
    string? ResolvedDestinationRoot,
    string MatchedRule,
    string TargetVolume)
{
    /// <summary>Projects a planned <paramref name="item"/> onto its wire shape.</summary>
    public static PreviewItemView From(RenamerPlanItem item) => new(
        item.FileId,
        item.OldFullPath,
        item.NewFullPath,
        item.Status,
        item.NewBasename,
        item.TargetFolderPath,
        item.Reason,
        item.Suffixed,
        item.Sanitized,
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
