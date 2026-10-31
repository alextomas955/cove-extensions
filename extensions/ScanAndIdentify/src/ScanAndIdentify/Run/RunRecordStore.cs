using System.Text.Json;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScanAndIdentify.Run;

/// <summary>Persists the <see cref="RunRecord"/> as a single JSON blob under its own store key.</summary>
/// <remarks>
/// Not a subclass of the shared options store: that type's key is the constant <c>"options"</c>,
/// read by name from other extensions, so widening it for a second use would reach outside this one.
/// <para>
/// <see cref="LoadAsync"/> answers null for an absent key and for a blob that cannot be honored, so a
/// caller reads a damaged record as "no run" rather than dereferencing a member the blob set to null.
/// </para>
/// </remarks>
public sealed class RunRecordStore(IExtensionStore store, ILogger? logger = null)
{
    /// <summary>The single store key the run blob lives under.</summary>
    public const string Key = "run";

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Reads the stored run, or null when none is stored or the stored one cannot be honored.
    /// </summary>
    /// <remarks>
    /// A blob is refused when it names no run, carries no stage list, or carries a list that is not
    /// the four stages in run order: the derivation addresses stages positionally, so a short or
    /// reordered list would read one stage's state onto another.
    /// </remarks>
    public async Task<RunRecord?> LoadAsync(CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        RunRecord? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<RunRecord>(json, RunRecord.JsonOptions);
        }
        catch (JsonException ex)
        {
            RunRecordStoreLog.StoredRunDiscarded(_logger, ex);
            return null;
        }

        if (loaded is null || string.IsNullOrWhiteSpace(loaded.RunId) || !HasEveryStageInOrder(loaded))
        {
            RunRecordStoreLog.StoredRunDiscarded(_logger, null);
            return null;
        }

        return loaded with { Options = loaded.Options ?? new() };
    }

    /// <summary>Writes the record over the single run key.</summary>
    public async Task SaveAsync(RunRecord record, CancellationToken ct = default)
        => await store.SetAsync(Key, JsonSerializer.Serialize(record, RunRecord.JsonOptions), ct);

    private static bool HasEveryStageInOrder(RunRecord record)
    {
        var stages = record.Stages;
        if (stages is null || stages.Count != RunRecord.StageOrder.Count)
        {
            return false;
        }

        for (int i = 0; i < stages.Count; i++)
        {
            if (stages[i] is null || stages[i].Stage != RunRecord.StageOrder[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The source-generated log message for the one failure the run store swallows.</summary>
internal static partial class RunRecordStoreLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "[run] the stored run blob could not be read and was discarded; "
            + "the page will report that no run has been started")]
    public static partial void StoredRunDiscarded(ILogger logger, Exception? ex);
}
