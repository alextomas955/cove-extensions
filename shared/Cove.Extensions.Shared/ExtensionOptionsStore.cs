using System.Text.Json;
using Cove.Plugins;

namespace Cove.Extensions.Shared;

/// <summary>
/// Thin async load/save layer for an extension's options model over Cove's
/// <see cref="IExtensionStore"/>. Stores a single JSON blob under the <c>"options"</c> key.
/// </summary>
/// <remarks>
/// Takes an <see cref="IExtensionStore"/> directly (not <c>FullExtensionBase.Store</c>) so it is
/// unit-testable host-free against an in-memory fake. <see cref="IExtensionStore"/> is fully async —
/// these methods never block on the store. Each extension supplies its own <typeparamref name="TOptions"/>
/// model, its own <see cref="JsonSerializerOptions"/>, and a default-value factory, so serialization
/// behavior is identical to a per-extension store.
/// </remarks>
/// <typeparam name="TOptions">The extension's options model.</typeparam>
public class ExtensionOptionsStore<TOptions>(
    IExtensionStore store,
    JsonSerializerOptions jsonOptions,
    Func<TOptions> defaultFactory)
    where TOptions : class
{
    private const string Key = "options";

    /// <summary>
    /// Loads the persisted options. Returns defaults when the key is absent (first run) or when the
    /// stored blob is corrupt (catches <see cref="JsonException"/>) — a hand-edited/garbage blob never throws.
    /// </summary>
    public async Task<TOptions> LoadAsync(CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return defaultFactory();
        }

        try
        {
            return JsonSerializer.Deserialize<TOptions>(json, jsonOptions) ?? defaultFactory();
        }
        catch (JsonException)
        {
            return defaultFactory(); // corrupt/hand-edited blob → safe defaults
        }
    }

    /// <summary>Serializes the options to the single <c>"options"</c> JSON blob.</summary>
    public async Task SaveAsync(TOptions options, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(options, jsonOptions);
        await store.SetAsync(Key, json, ct);
    }
}
