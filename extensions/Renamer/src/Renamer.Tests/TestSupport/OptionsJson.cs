using System.Text.Json;
using System.Text.Json.Nodes;
using Renamer.Options;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Compares <see cref="RenamerOptions"/> instances by the document they persist as.
/// </summary>
/// <remarks>
/// The persisted blob is the contract the panel and the store share, so comparing it covers every
/// member the model declares without a projection that a member added later could be left out of.
/// Object members are ordered by name because a <see cref="Dictionary{TKey,TValue}"/> carries no
/// guaranteed order and a round-trip may reorder its keys.
/// </remarks>
internal static class OptionsJson
{
    /// <summary>The document <paramref name="options"/> persists as. A null instance fails the comparison rather than matching one.</summary>
    public static string Canonical(RenamerOptions? options)
    {
        Assert.NotNull(options);
        return Sorted(JsonSerializer.SerializeToNode(options, RenamerOptions.JsonOptions))
            ?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
    }

    /// <summary>Serializes, reads back, and returns the reloaded instance, failing if the document changed.</summary>
    public static RenamerOptions AssertRoundTrips(RenamerOptions original)
    {
        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);
        Assert.NotNull(reloaded);
        Assert.Equal(Canonical(original), Canonical(reloaded));
        return reloaded;
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
