using System.Text.Json;
using Cove.Plugins;

namespace WhisparrSync.Options;

/// <summary>
/// Thin async load/save layer for <see cref="WhisparrOptions"/> over Cove's <see cref="IExtensionStore"/>.
/// Stores a single JSON blob under the <c>"options"</c> key.
/// </summary>
/// <remarks>
/// Takes an <see cref="IExtensionStore"/> directly (not <c>FullExtensionBase.Store</c>) so it is unit
/// testable host-free against an in-memory fake. <see cref="IExtensionStore"/> is fully async — these
/// methods never block on the store.
/// </remarks>
public sealed class OptionsStore(IExtensionStore store)
{
    private const string Key = "options";

    /// <summary>
    /// Loads the persisted options. Returns defaults when the key is absent (first run) or when the stored
    /// blob is corrupt (catches <see cref="JsonException"/>) — a hand-edited/garbage blob never throws.
    /// </summary>
    public async Task<WhisparrOptions> LoadAsync(CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new WhisparrOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<WhisparrOptions>(json, WhisparrOptions.JsonOptions) ?? new WhisparrOptions();
        }
        catch (JsonException)
        {
            return new WhisparrOptions(); // corrupt/hand-edited blob → safe defaults
        }
    }

    /// <summary>Serializes the options to the single <c>"options"</c> JSON blob.</summary>
    public async Task SaveAsync(WhisparrOptions options, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(options, WhisparrOptions.JsonOptions);
        await store.SetAsync(Key, json, ct);
    }
}
