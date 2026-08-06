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
        Message = "[Renamer] scan library: complete — {Files} file(s) planned from {Total} item(s)")]
    private partial void LogScanDone(int files, int total);

    [LoggerMessage(
        EventId = 1053, Level = LogLevel.Warning,
        Message = "[Renamer] could not delete the legacy per-file scan result; the settings page may stay unreadable")]
    private partial void LogLegacyScanPurgeFailed(Exception ex);

    [LoggerMessage(
        EventId = 1054, Level = LogLevel.Warning,
        Message = "[Renamer] could not discard the pre-upgrade undo journal; the settings page may stay unreadable until the next load retries")]
    private partial void LogRevertLogPurgeFailed(Exception ex);

    [LoggerMessage(
        EventId = 1055, Level = LogLevel.Information,
        Message = "[Renamer] batch {RunId}: {Files} file(s) exceeds the {Cap}-file undo cap — this batch is not undoable")]
    private partial void LogBatchNotJournalled(string runId, int files, int cap);

    // The one-time name→id options conversion rewrites the stored settings IN PLACE and keeps no copy
    // of the originals, so these lines are the whole forensic trail: what it resolved against, what it
    // discarded, and why it refused when it did. The converted/dropped pair is written between the
    // settings write and the stamp write, so its presence is also what says the rewrite happened.

    [LoggerMessage(
        EventId = 1056, Level = LogLevel.Information,
        Message = "[Renamer] options migration: converted against {Tags} tag(s) and {Performers} performer(s); {Dropped} name(s) dropped")]
    private partial void LogOptionsMigrationConverted(int tags, int performers, int dropped);

    [LoggerMessage(
        EventId = 1057, Level = LogLevel.Information,
        Message = "[Renamer] options migration: {Count} stored rule name(s) matched no tag or performer and were dropped: {Names}")]
    private partial void LogOptionsMigrationDroppedNames(int count, string names);

    // Matching was case-insensitive before the migration, so a rule stored as "4K" suppressed every tag
    // whose name equalled it in any case. Keyed on an id it now suppresses one of them, and because the
    // name still RESOLVED nothing is dropped — this line is the only place that narrowing surfaces.
    [LoggerMessage(
        EventId = 1061, Level = LogLevel.Warning,
        Message = "[Renamer] options migration: {Count} stored rule name(s) matched several entities differing only by letter case and now match one: {Detail}")]
    private partial void LogOptionsMigrationNarrowedNames(int count, string detail);

    [LoggerMessage(
        EventId = 1058, Level = LogLevel.Warning,
        Message = "[Renamer] options migration deferred ({Reason}); the stored settings are unchanged and the next load retries")]
    private partial void LogOptionsMigrationDeferred(string reason);

    [LoggerMessage(
        EventId = 1059, Level = LogLevel.Warning,
        Message = "[Renamer] options migration failed before it recorded a conversion; the next load retries")]
    private partial void LogOptionsMigrationFailed(Exception ex);

    [LoggerMessage(
        EventId = 1060, Level = LogLevel.Warning,
        Message = "[Renamer] options migration rewrote the stored settings but could not stamp them as converted; the next load re-scans an already-converted blob and changes nothing")]
    private partial void LogOptionsMigrationStampFailed(Exception ex);

    [LoggerMessage(
        EventId = 1010, Level = LogLevel.Information,
        Message = "[Renamer] undo {RunId}: {Kind} id={EntityId} restored to '{Old}'")]
    private partial void LogUndoRestored(string runId, RenamerFileKind kind, int entityId, string old);

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
