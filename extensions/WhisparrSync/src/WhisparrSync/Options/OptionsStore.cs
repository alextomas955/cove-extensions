using System.Text.Json;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WhisparrSync.Options;

/// <summary>The options a load answered, and whether the stored blob is what produced them.</summary>
/// <param name="Options">The options as the load produced them.</param>
/// <param name="Bound">
/// False when a blob is stored and the model could not bind it, so <paramref name="Options"/> holds
/// the defaults the load manufactured rather than anything a user configured. True for a store that
/// has never been written to, whose defaults are the correct answer.
/// </param>
public sealed record OptionsLoad(WhisparrSyncOptions Options, bool Bound);

/// <summary>
/// Persists <see cref="WhisparrSyncOptions"/> as a single JSON blob under the <c>"options"</c> key: a
/// binding of the shared <see cref="ExtensionOptionsStore{TOptions}"/> to this extension's options
/// model and its <see cref="WhisparrSyncOptions.JsonOptions"/>.
/// </summary>
/// <remarks>
/// No normaliser is supplied: no member of the model can hold a stored value the extension cannot
/// honour, so there is nothing for a load-time rule to replace.
/// </remarks>
public sealed class OptionsStore : ExtensionOptionsStore<WhisparrSyncOptions>
{
    private readonly IExtensionStore _store;

    public OptionsStore(IExtensionStore store, ILogger? logger = null)
        : base(
            store,
            WhisparrSyncOptions.JsonOptions,
            static () => new WhisparrSyncOptions(),
            logger ?? NullLogger.Instance)
        => _store = store;

    /// <summary>
    /// Loads the persisted options and reports whether they were bound from the stored blob.
    /// </summary>
    /// <remarks>
    /// The base load answers a blob the model cannot bind with defaults, which every layer above
    /// reads exactly as it reads a store nobody has written to yet. A writer that cannot tell those
    /// apart folds onto the defaults and saves them over the stored configuration.
    /// </remarks>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The options, and whether the stored blob bound.</returns>
    public async Task<OptionsLoad> LoadBoundAsync(CancellationToken ct = default)
    {
        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        var json = await _store.GetAsync(Key, ct).ConfigureAwait(false);
        return new OptionsLoad(loaded, string.IsNullOrWhiteSpace(json) || Binds(json));
    }

    private static bool Binds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WhisparrSyncOptions>(json, WhisparrSyncOptions.JsonOptions)
                is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
