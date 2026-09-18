using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Renamer.Options;

/// <summary>
/// Persists <see cref="RenamerOptions"/> as a single JSON blob under the <c>"options"</c> key.
/// </summary>
public sealed class OptionsStore(IExtensionStore store, ILogger? logger = null)
    : ExtensionOptionsStore<RenamerOptions>(
        store,
        RenamerOptions.JsonOptions,
        static () => new RenamerOptions(),
        logger ?? NullLogger.Instance,
        static o => o.WithUsableLengthCaps());
