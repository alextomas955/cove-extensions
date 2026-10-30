using System.Text.Json;

namespace ScanAndIdentify.Options;

/// <summary>What one run does: which passes it performs and how each of them behaves.</summary>
/// <remarks>
/// The member names are the host request-body field names, so a run copies them across with no
/// translation table.
/// </remarks>
public sealed record RunOptions
{
    /// <summary>Generate the fingerprints a later identify pass matches on, where they are missing.</summary>
    public bool BackfillFingerprints { get; init; } = true;

    /// <summary>Serializer settings shared by save and load, so the round-trip is symmetric.</summary>
    /// <remarks>
    /// No naming policy, so the stored blob carries this record's own PascalCase spelling, which is
    /// not the camelCase the routes answer in. Case-insensitive names keep a hand-edited blob readable.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
