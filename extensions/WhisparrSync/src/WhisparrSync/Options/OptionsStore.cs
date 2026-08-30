using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WhisparrSync.Options;

/// <summary>
/// Persists <see cref="WhisparrSyncOptions"/> as a single JSON blob under the <c>"options"</c> key: a
/// binding of the shared <see cref="ExtensionOptionsStore{TOptions}"/> to this extension's options
/// model and its <see cref="WhisparrSyncOptions.JsonOptions"/>.
/// </summary>
/// <remarks>
/// No normaliser is supplied: no member of the model can hold a stored value the extension cannot
/// honour, so there is nothing for a load-time rule to replace.
/// </remarks>
public sealed class OptionsStore(IExtensionStore store, ILogger? logger = null)
    : ExtensionOptionsStore<WhisparrSyncOptions>(
        store,
        WhisparrSyncOptions.JsonOptions,
        static () => new WhisparrSyncOptions(),
        logger ?? NullLogger.Instance);
