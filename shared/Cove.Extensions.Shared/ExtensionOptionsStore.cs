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
    /// <summary>The single store key the whole options blob lives under.</summary>
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
            // panel, an API response and an end-to-end test that all agree on the wrong answer. The
            // silent version of this line cost a review lane three separate "routing bugs" that were one
            // failed bind. The blob itself is never logged — it is the user's configuration.
            ExtensionOptionsStoreLog.StoredOptionsDiscarded(logger, typeof(TOptions).Name, ex);
            return defaultFactory();
        }
    }

    /// <summary>
    /// The stored blob verbatim, or <c>null</c> when the key is absent.
    /// </summary>
    /// <remarks>
    /// The seam a one-time schema conversion needs: a blob written under an older shape may not bind to
    /// the current <typeparamref name="TOptions"/> at all, and <see cref="LoadAsync"/> answers that with
    /// DEFAULTS — so a converter that went through it would convert defaults and then persist them over
    /// the user's settings. Reading raw also lets a converter carry through keys it does not model.
    /// </remarks>
    public Task<string?> LoadRawAsync(CancellationToken ct = default) => store.GetAsync(Key, ct);

    /// <summary>
    /// Overwrites the stored blob with <paramref name="json"/> verbatim, bypassing serialization.
    /// </summary>
    /// <remarks>The write half of <see cref="LoadRawAsync"/>; same one-time-conversion rationale.</remarks>
    public Task SaveRawAsync(string json, CancellationToken ct = default) => store.SetAsync(Key, json, ct);

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
