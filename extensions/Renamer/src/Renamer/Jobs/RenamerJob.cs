using System.Text.Json;

namespace Renamer.Jobs;

// The host's job-parameter map holds strings only, so the id list is JSON-encoded into a single
// value to round-trip through it.
public static class RenamerJob
{
    public const string JobId = "renamer-batch";

    // The entity type travels raw, lowercase singular.
    private const string EntityTypeKey = "entityType";

    private const string EntityIdsKey = "entityIds";

    public static Dictionary<string, string> Encode(string entityType, IReadOnlyList<int> ids) => new()
    {
        [EntityTypeKey] = entityType,
        [EntityIdsKey] = JsonSerializer.Serialize(ids),
    };

    // Never throws, so a malformed batch is a clean no-op: a missing entity type decodes to an empty
    // string, and a missing, blank or unparseable id list to an empty array.
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
