using System.Text.Json;

namespace Renamer.Jobs;

/// <summary>
/// The batch-renamer job's id constant plus the pure (de)serialization helpers for the host's
/// <c>IReadOnlyDictionary&lt;string,string&gt;?</c> job-parameter encoding. The id list is
/// JSON-encoded into a single string value so it round-trips through the host's string-only
/// parameter map.
/// </summary>
public static class RenamerJob
{
    /// <summary>The single <c>ExtensionJobDefinition</c> id for the batch-renamer job.</summary>
    public const string JobId = "renamer-batch";

    /// <summary>The parameter key carrying the raw lowercase-singular entity type.</summary>
    private const string EntityTypeKey = "entityType";

    /// <summary>The parameter key carrying the JSON-serialized int id array.</summary>
    private const string EntityIdsKey = "entityIds";

    /// <summary>
    /// Encodes an entity type + id list into the host's string-only parameter map:
    /// <c>"entityType"</c> = the raw string, <c>"entityIds"</c> = a JSON int array.
    /// </summary>
    public static Dictionary<string, string> Encode(string entityType, IReadOnlyList<int> ids) => new()
    {
        [EntityTypeKey] = entityType,
        [EntityIdsKey] = JsonSerializer.Serialize(ids),
    };

    /// <summary>
    /// Decodes the host parameter map back into <c>(entityType, ids)</c>. Tolerant of bad/empty
    /// input: a missing <c>entityType</c> yields an empty string; a missing/null/blank/unparseable
    /// <c>entityIds</c> yields an empty array — never throws, so a bad batch is a clean no-op.
    /// </summary>
    public static (string entityType, int[] ids) Decode(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null)
        {
            return (string.Empty, []);
        }

        var entityType = parameters.TryGetValue(EntityTypeKey, out var et) ? et ?? string.Empty : string.Empty;

        if (!parameters.TryGetValue(EntityIdsKey, out var rawIds) || string.IsNullOrWhiteSpace(rawIds))
        {
            return (entityType, []);
        }

        try
        {
            var ids = JsonSerializer.Deserialize<int[]>(rawIds);
            return (entityType, ids ?? []);
        }
        catch (JsonException)
        {
            return (entityType, []);
        }
    }
}
