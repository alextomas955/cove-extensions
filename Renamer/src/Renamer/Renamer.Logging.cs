using Microsoft.Extensions.Logging;
using Renamer.Planner;

namespace Renamer;

/// <summary>
/// Source-generated, high-performance log messages for the renamer slice. Renamers and moves change
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
    private partial void LogItemRenamerd(
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
        Message = "[Renamer] batch {RunId} done: {Renamerd} renamerd, {Skipped} skipped, {Failed} failed")]
    private partial void LogBatchDone(string runId, int renamerd, int skipped, int failed);

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
    private partial void LogAutoRenamerd(
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
