using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WhisparrSync.Options;

/// <summary>The options a load answered, and whether the stored blob is what produced them.</summary>
/// <remarks>
/// Bound is false when a blob is stored and the model could not bind it, so the options are the
/// defaults the load manufactured rather than anything a user configured. It is true for a store
/// that has never been written to, whose defaults are the correct answer.
/// </remarks>
public sealed record OptionsLoad(WhisparrSyncOptions Options, bool Bound);

/// <summary>
/// Persists <see cref="WhisparrSyncOptions"/> as a single JSON blob under the <c>"options"</c> key.
/// </summary>
public sealed class OptionsStore(
    IExtensionStore store,
    ILogger? logger = null,
    Action<string?>? publishGeneration = null)
    : ExtensionOptionsStore<WhisparrSyncOptions>(
        store,
        WhisparrSyncOptions.JsonOptions,
        static () => new WhisparrSyncOptions(),
        logger ?? NullLogger.Instance)
{
    /// <summary>
    /// Loads the persisted options and reports whether they were bound from the stored blob.
    /// </summary>
    /// <remarks>
    /// The base load answers a blob the model cannot bind with defaults, which reads exactly as a
    /// store nobody has written to yet. A writer that cannot tell those apart folds onto the
    /// defaults and saves them over the stored configuration.
    /// </remarks>
    public async Task<OptionsLoad> LoadBoundAsync(CancellationToken ct = default)
    {
        var read = await LoadReportedAsync(ct).ConfigureAwait(false);
        return new OptionsLoad(read.Options, read.Bound);
    }

    // Publishes the generation each load establishes, or null where nothing was: a blob the model
    // could not bind establishes nothing, since its options are manufactured defaults. Covers a
    // caller that writes the store through the host's own extension-data route without reaching
    // this extension's save.
    protected override void OnLoaded(WhisparrSyncOptions options, bool bound)
        => publishGeneration?.Invoke(bound ? options.SelectedGeneration.ToString() : null);
}
