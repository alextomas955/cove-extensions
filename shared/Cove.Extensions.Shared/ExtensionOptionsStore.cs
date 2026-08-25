using System.Text.Json;
using Cove.Plugins;
using Microsoft.Extensions.Logging;

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
    Func<TOptions> defaultFactory,
    ILogger logger)
    where TOptions : class
{
    /// <summary>The single store key the options blob lives under.</summary>
    /// <remarks>
    /// Public because a one-time conversion has to reach the RAW blob: a legacy shape does not bind to
    /// the current model, and <see cref="LoadAsync"/> answers a bind failure with defaults, so a
    /// converter going through the typed load would rewrite defaults over the stored configuration.
    /// </remarks>
    public const string Key = "options";

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
        catch (JsonException ex)
        {
            // Loud, because this is the one place a whole stored configuration can be discarded with
            // nothing observable happening: defaults are indistinguishable from a correct empty
            // configuration at every layer above, so the symptom is settings that read as unset and a
            // panel, an API response and an end-to-end test that all agree on the wrong answer. The blob
            // itself is never logged — it is the user's configuration.
            ExtensionOptionsStoreLog.StoredOptionsDiscarded(logger, typeof(TOptions).Name, ex);
            return defaultFactory();
        }
    }

    /// <summary>Serializes the options to the single <c>"options"</c> JSON blob.</summary>
    public async Task SaveAsync(TOptions options, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(options, jsonOptions);
        await store.SetAsync(Key, json, ct);
    }
}

/// <summary>
/// The source-generated log message for the one failure <see cref="ExtensionOptionsStore{TOptions}"/>
/// swallows. Non-generic and static because the generator's partial methods are declared per type and a
/// generic owner buys nothing here — the type being loaded travels as an argument instead.
/// </summary>
internal static partial class ExtensionOptionsStoreLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "[options] the stored {Options} blob could not be read and DEFAULTS were used instead; "
            + "every configured setting will read as unset until it is saved again")]
    public static partial void StoredOptionsDiscarded(ILogger logger, string options, Exception ex);
}
