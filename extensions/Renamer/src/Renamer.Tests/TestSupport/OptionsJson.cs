using System.Text.Json;
using System.Text.Json.Nodes;
using Renamer.Options;

namespace Renamer.Tests.TestSupport;

// Compares RenamerOptions instances by the document they persist as. The persisted blob is the
// contract the panel and the store share, so comparing it covers every member the model declares
// without a projection that a member added later could be left out of. Object members are ordered
// by name because a Dictionary{TKey,TValue} carries no guaranteed order and a round-trip may
// reorder its keys.
internal static class OptionsJson
{
    // The document options persists as. A null instance fails the comparison rather than matching
    // one.
    public static string Canonical(RenamerOptions? options)
    {
        Assert.NotNull(options);
        return Sorted(JsonSerializer.SerializeToNode(options, RenamerOptions.JsonOptions))
            ?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
    }

    private static JsonNode? Sorted(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var sorted = new JsonObject();
                foreach (var member in o.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    sorted[member.Key] = Sorted(member.Value?.DeepClone());
                }

                return sorted;
            case JsonArray a:
                return new JsonArray(a.Select(e => Sorted(e?.DeepClone())).ToArray());
            default:
                return node?.DeepClone();
        }
    }
}
