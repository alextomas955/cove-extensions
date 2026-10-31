using System.Text.Json;
using ScanAndIdentify.Contracts;
using ScanAndIdentify.Options;

namespace ScanAndIdentify.Run;

/// <summary>One stage of a run as it is stored.</summary>
/// <remarks>
/// <c>JobId</c> stays null until a caller reports the host job it started, which is the window the
/// <c>Starting</c> state exists to make visible. <c>EndedAt</c> and <c>Detail</c> are both written
/// the first time the stage is observed in a terminal state, and <c>Detail</c> stays null for a
/// stage no read ever saw a job for.
/// </remarks>
public sealed record RunStageEntry
{
    public RunStage Stage { get; init; }

    public RunStageState State { get; init; }

    public string? JobId { get; init; }

    public DateTime? StartedAt { get; init; }

    public DateTime? EndedAt { get; init; }

    public RunStageDetail? Detail { get; init; }
}

/// <summary>The persisted description of one run: what it was started with and where it has got to.</summary>
/// <remarks>
/// Fixed size. Nothing here is per file, so the blob does not grow with the library.
/// <para>
/// A stage's terminal state is persisted the first time it is observed rather than re-derived on
/// every read, because the host forgets a finished job once a hundred others have finished and a run
/// read later would otherwise report its own completed stages as unknown.
/// </para>
/// </remarks>
public sealed record RunRecord
{
    /// <summary>The stages every record carries, in the order a run performs them.</summary>
    public static IReadOnlyList<RunStage> StageOrder { get; } =
        [RunStage.Scan, RunStage.Fingerprints, RunStage.Identify, RunStage.Images];

    public string RunId { get; init; } = string.Empty;

    public DateTime StartedAt { get; init; }

    public DateTime? EndedAt { get; init; }

    public RunEndReason? EndedReason { get; init; }

    /// <summary>The options read when the run was claimed, which every stage's request body is built from.</summary>
    public RunOptions Options { get; init; } = new();

    public List<RunStageEntry> Stages { get; init; } = [];

    /// <summary>Serializer settings shared by save and load, so the round trip is symmetric.</summary>
    /// <remarks>
    /// No naming policy, so the stored blob carries this record's own PascalCase spelling rather than
    /// the camelCase the routes answer in. The stage and state enums carry their own converter, so
    /// both spellings are declared once.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
