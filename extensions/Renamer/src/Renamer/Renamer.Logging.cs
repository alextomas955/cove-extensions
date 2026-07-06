using Microsoft.Extensions.Logging;
using Renamer.Planner;

namespace Renamer;

/// <summary>
/// Source-generated, high-performance log messages for the renamer slice. Renames and moves change
/// files on disk, so every batch/undo/auto-renamer records what it did to Cove's normal log — per-file
/// old → new, skip reasons, and a summary — for audit and troubleshooting.
///
/// These use the <see cref="LoggerMessageAttribute"/> source generator (the pattern the analyzers
/// require, CA1848/CA1873): each call site is a strongly-typed method with no boxing and no argument
/// evaluation when the level is disabled. Keeping them here keeps the call sites in
/// <c>Renamer.cs</c> / <c>Renamer.Api.cs</c> / <c>Renamer.Events.cs</c> terse.
/// </summary>
public sealed partial class Renamer
{
    [LoggerMessage(
        EventId = 1000, Level = LogLevel.Information,
        Message = "[Renamer] batch {RunId} ({Kind}) started: {Count} item(s)")]
    private partial void LogBatchStarted(string runId, RenamerFileKind kind, int count);

    [LoggerMessage(
        EventId = 1001, Level = LogLevel.Information,
        Message = "[Renamer] batch {RunId}: {Kind} id={EntityId} {Status} '{Old}' -> '{New}'")]
    private partial void LogItemRenamed(
        string runId, RenamerFileKind kind, int entityId, RenamerStatus status, string old, string @new);

    [LoggerMessage(
        EventId = 1002, Level = LogLevel.Information,
        Message = "[Renamer] batch {RunId}: {Kind} id={EntityId} skipped ({Status}): {Reason}")]
    private partial void LogItemSkipped(
        string runId, RenamerFileKind kind, int entityId, RenamerStatus status, string reason);

    [LoggerMessage(
        EventId = 1003, Level = LogLevel.Warning,
        Message = "[Renamer] batch {RunId}: {Kind} id={EntityId} FAILED '{Old}' -> '{New}': {Reason}")]
    private partial void LogItemFailed(
        string runId, RenamerFileKind kind, int entityId, string old, string @new, string reason);

    [LoggerMessage(
        EventId = 1004, Level = LogLevel.Information,
        Message = "[Renamer] batch {RunId} done: {Renamed} renamed, {Skipped} skipped, {Failed} failed")]
    private partial void LogBatchDone(string runId, int renamed, int skipped, int failed);

    // PHASE A of RunRenamerBatchAsync plans + classifies every id sequentially and reports NO progress
    // percentage until PHASE B, so a large library sits at 0% with no visible signal. These trace the
    // planning phase to Cove's log so a long 0% is legible as "still planning N of M", not a hang.

    [LoggerMessage(
        EventId = 1005, Level = LogLevel.Information,
        Message = "[Renamer] batch {RunId} ({Kind}): planning {Count} item(s)…")]
    private partial void LogPlanningStarted(string runId, RenamerFileKind kind, int count);

    [LoggerMessage(
        EventId = 1006, Level = LogLevel.Information,
        Message = "[Renamer] batch {RunId}: planning {Index}/{Count} id={EntityId} — {ActingFiles} file(s) will act")]
    private partial void LogItemPlanned(string runId, int index, int count, int entityId, int actingFiles);

    [LoggerMessage(
        EventId = 1007, Level = LogLevel.Information,
        Message = "[Renamer] batch {RunId}: planning complete — {Acting} file(s) will act across {Planned} item(s)")]
    private partial void LogPlanningDone(string runId, int acting, int planned);

    // Logged BEFORE a move runs, so a cross-volume copy (a full copy→verify→delete that can take many
    // seconds for a large file) is legible as "copying now", not a frozen bar. A same-volume rename is
    // near-instant, so the CrossVolume flag lets the reader tell a slow copy from a quick rename.
    [LoggerMessage(
        EventId = 1008, Level = LogLevel.Information,
        Message = "[Renamer] batch {RunId}: {Done}/{Total} starting {Kind} id={EntityId} (crossVolume={CrossVolume}, {SizeMb} MB) '{Old}'")]
    private partial void LogItemStarting(
        string runId, int done, int total, RenamerFileKind kind, int entityId, bool crossVolume, long sizeMb, string old);

    // RunRenamerLibraryJobAsync fans out one batch PER KIND over the whole library; without these the
    // only signal is the inner per-batch lines, so the outer "which kind, how many" framing is invisible.

    [LoggerMessage(
        EventId = 1040, Level = LogLevel.Information,
        Message = "[Renamer] library renamer: {Kind} — {Count} item(s) to plan")]
    private partial void LogLibraryKind(RenamerFileKind kind, int count);

    // The whole-library SCAN (dry run) planned every entity with a single Report(1.0) at the end, so the
    // job jumped 0%→100% with no intermediate feedback. These trace the scan and match the per-item
    // planning trace the rename batch already emits, so a large scan is legible in Cove's log.

    [LoggerMessage(
        EventId = 1050, Level = LogLevel.Information,
        Message = "[Renamer] scan library: {Total} item(s) across {Kinds} kind(s) to plan")]
    private partial void LogScanStarted(int total, int kinds);

    [LoggerMessage(
        EventId = 1051, Level = LogLevel.Information,
        Message = "[Renamer] scan library: planned {Done}/{Total} ({Kind} id={EntityId})")]
    private partial void LogScanItemPlanned(int done, int total, RenamerFileKind kind, int entityId);

    [LoggerMessage(
        EventId = 1052, Level = LogLevel.Information,
        Message = "[Renamer] scan library: complete — {Rows} row(s) from {Total} item(s)")]
    private partial void LogScanDone(int rows, int total);

    [LoggerMessage(
        EventId = 1010, Level = LogLevel.Information,
        Message = "[Renamer] undo {RunId}: {Kind} id={EntityId} restored '{New}' -> '{Old}'")]
    private partial void LogUndoRestored(
        string runId, RenamerFileKind kind, int entityId, string @new, string old);

    [LoggerMessage(
        EventId = 1011, Level = LogLevel.Information,
        Message = "[Renamer] undo {RunId}: file id={FileId} skipped: {Reason}")]
    private partial void LogUndoSkipped(string runId, int fileId, string reason);

    [LoggerMessage(
        EventId = 1012, Level = LogLevel.Warning,
        Message = "[Renamer] undo {RunId}: file id={FileId} FAILED: {Reason}")]
    private partial void LogUndoFailed(string runId, int fileId, string reason);

    [LoggerMessage(
        EventId = 1013, Level = LogLevel.Information,
        Message = "[Renamer] undo {RunId} done: {Undone} restored, {Skipped} skipped, {Failed} failed")]
    private partial void LogUndoDone(string runId, int undone, int skipped, int failed);

    [LoggerMessage(
        EventId = 1020, Level = LogLevel.Information,
        Message = "[Renamer] auto-renamer: {Kind} id={EntityId} {Status} '{Old}' -> '{New}'")]
    private partial void LogAutoRenamed(
        RenamerFileKind kind, int entityId, RenamerStatus status, string old, string @new);

    [LoggerMessage(
        EventId = 1021, Level = LogLevel.Warning,
        Message = "[Renamer] auto-renamer: {Kind} id={EntityId} FAILED '{Old}' -> '{New}': {Reason}")]
    private partial void LogAutoRenamerFailed(
        RenamerFileKind kind, int entityId, string old, string @new, string reason);

    [LoggerMessage(
        EventId = 1022, Level = LogLevel.Error,
        Message = "[Renamer] auto-renamer failed for {Kind} id={EntityId}")]
    private partial void LogAutoRenamerError(Exception ex, RenamerFileKind kind, int entityId);

    [LoggerMessage(
        EventId = 1030, Level = LogLevel.Warning,
        Message = "[Renamer] routing: skipped invalid source-path regex '{Pattern}': {Reason}")]
    private partial void LogInvalidRouteRegex(string pattern, string reason);
}
