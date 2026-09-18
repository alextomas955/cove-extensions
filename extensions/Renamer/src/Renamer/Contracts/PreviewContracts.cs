using System.Text.Json;
using Renamer.Planner;

namespace Renamer.Contracts;

/// <summary>The wire projection of a <see cref="RenamerPlanItem"/> the preview response serializes.</summary>
/// <remarks>
/// The property names are the wire contract the UI reads, serialized camelCase, and <see cref="From"/>
/// is the only mapping from the domain item. Paths are forward-slash, <c>NewFullPath</c> repeats the
/// old path for a no-op or skip, <c>Reason</c> is null for a plain rename or move, and
/// <c>ResolvedDestinationRoot</c> is null for a source-confined or in-place item.
/// <c>InFlightPathOverflow</c> says the item crosses volumes and its in-flight copy would overrun the
/// path budget, so the move cannot complete even though the planned path fits.
/// </remarks>
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
    /// <summary>Projects a planned item onto its wire shape.</summary>
    /// <remarks>
    /// <paramref name="inFlightPathOverflow"/> is passed in because the answer needs the volume
    /// classification and the configured path budget. The caller computes it from
    /// <c>BatchPreview.InFlightPathOverflows</c>, the comparison the aggregate's count also reads, so
    /// the count and the flags cannot disagree.
    /// </remarks>
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

/// <summary>The <c>/preview</c> response body: the per-item plan and the whole-batch blast radius.</summary>
/// <remarks>
/// <c>Items</c> holds one <see cref="PreviewItemView"/> per physical file of the selection, in plan
/// order. Both halves ride the same camelCase serializer, and
/// <see cref="PreviewSummary.ConfirmLevel"/> takes its camelCase wire spelling from the converter
/// declared on the enum type.
/// </remarks>
public sealed record PreviewResponse(
    IReadOnlyList<PreviewItemView> Items,
    PreviewSummary Summary);

/// <summary>One built-in sample's live-preview result, rendered by the real engine.</summary>
/// <remarks>
/// <c>NewName</c> includes the extension and <c>Folder</c> is relative, empty meaning no folder move.
/// <c>Flags</c> carries the stable codes <c>empty</c>, <c>sanitized</c>, <c>length-reduced</c> and
/// <c>gating-skip</c>, in no significant order. <c>DroppedFields</c> lists the
/// <see cref="Options.RenamerOptions.DropOrder"/> fields the engine dropped when <c>Flags</c> holds
/// <c>length-reduced</c>, and is empty otherwise.
/// </remarks>
public sealed record PreviewSampleResult(
    string SampleLabel,
    string OldName,
    string NewName,
    string Folder,
    string[] Flags,
    string[] DroppedFields);

/// <summary>The JSON shape the <c>/undo</c> endpoint returns.</summary>
/// <remarks>
/// Each problem channel is a total paired with a sample of at most
/// <c>UndoRunAccumulator.MaxSampleEntries</c> entries, in the order the run hit them, because a batch
/// reaches library size and an entry per problem would be a payload proportional to the library.
/// <c>Undone</c> counts entries restored on disk and in the database and is never capped. A failed
/// entry moved back but threw on the database save, so its disk state rolled forward again. A skipped
/// entry found its original slot occupied or locked and was never clobbered. A warning entry was
/// restored but left a companion file behind. A call with no batch, an empty log, or a second undo
/// returns <c>Undone</c> zero with zero totals.
/// </remarks>
public sealed record UndoResult(
    int Undone,
    int FailedCount,
    IReadOnlyList<UndoEntryError> FailedSample,
    int SkippedCount,
    IReadOnlyList<UndoEntryError> SkippedSample,
    int WarningCount,
    IReadOnlyList<UndoEntryWarning> WarningSample);


/// <summary>One restored-but-incomplete entry surfaced in <see cref="UndoResult"/>.</summary>
/// <remarks>
/// There is no path pair, because the media file did return to its original path. Only a companion
/// did not, and <c>Detail</c> names which and why.
/// </remarks>
public sealed record UndoEntryWarning(int FileId, string Detail);

/// <summary>One failed or skipped reverse-replay entry surfaced in <see cref="UndoResult"/>.</summary>
/// <remarks>
/// <c>OldPath</c> is the original location the reverse move targeted and <c>NewPath</c> is the renamed
/// location the file currently sits at.
/// </remarks>
public sealed record UndoEntryError(int FileId, string OldPath, string NewPath, string Reason);


/// <summary>The JSON shape the <c>/last-batch</c> endpoint returns, for the undo panel.</summary>
/// <remarks>
/// Counts only: never a path, never a kind, never a per-file collection, which is what lets the
/// endpoint keep its coarse any-renamer-read gate and keeps the response size independent of the
/// library. <c>Count</c> is what the batch journalled and is never decremented as files are restored;
/// <c>RemainingCount</c> is derived server-side, so remaining plus restored plus unrestorable equals
/// it. With no batch, <c>HasBatch</c> is false and the other fields are zero or false.
/// </remarks>
public sealed record LastBatchSummary(
    bool HasBatch,
    int Count,
    int RemainingCount,
    int UnrestorableCount,
    long WrittenAtUtcTicks,
    bool Consumed);


/// <summary>The wire-serialization home for Renamer's Cove-facing response DTOs.</summary>
public static class PreviewContracts
{
    /// <summary>
    /// The host's wire convention, for the places this extension serializes something itself.
    /// </summary>
    /// <remarks>
    /// It carries no converter. Enum wire spelling comes from
    /// <see cref="Cove.Extensions.Shared.CamelCaseStringEnumConverter"/> declared on each enum type,
    /// and a converter here would outrank that one.
    /// </remarks>
    public static readonly JsonSerializerOptions PreviewResponseJsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>The job id a caller polls after an enqueue route accepts the work.</summary>
public sealed record JobEnqueued(string JobId);
