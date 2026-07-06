using System.Text.Json;
using Cove.Plugins;

namespace Renamer.Options;

/// <summary>
/// Thin async load/save layer for <see cref="RenamerOptions"/> over Cove's
/// <see cref="IExtensionStore"/>. Stores a single JSON blob under the <c>"options"</c> key.
/// </summary>
/// <remarks>
/// Takes an <see cref="IExtensionStore"/> directly (not the <c>FullExtensionBase.Store</c>)
/// so it is unit-testable host-free against an in-memory fake. <see cref="IExtensionStore"/>
/// is fully async — these methods never block on the store.
/// </remarks>
public sealed class OptionsStore(IExtensionStore store)
{
    private const string Key = "options";

    /// <summary>
    /// Loads the persisted options. Returns defaults when the key is absent (first run)
    /// or when the stored blob is corrupt (catches <see cref="JsonException"/>).
    /// </summary>
    public async Task<RenamerOptions> LoadAsync(CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RenamerOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions) ?? new RenamerOptions();
        }
        catch (JsonException)
        {
            return new RenamerOptions(); // corrupt/hand-edited blob → safe defaults
        }
    }

    /// <summary>Serializes the options to the single <c>"options"</c> JSON blob.</summary>
    public async Task SaveAsync(RenamerOptions options, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(options, RenamerOptions.JsonOptions);
        await store.SetAsync(Key, json, ct);
    }
}
