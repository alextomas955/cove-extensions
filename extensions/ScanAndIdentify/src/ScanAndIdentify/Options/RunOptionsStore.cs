using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScanAndIdentify.Options;

/// <summary>
/// Persists <see cref="RunOptions"/> as a single JSON blob under the <c>"options"</c> key.
/// </summary>
public sealed class RunOptionsStore(IExtensionStore store, ILogger? logger = null)
    : ExtensionOptionsStore<RunOptions>(
        store,
        RunOptions.JsonOptions,
        static () => new RunOptions(),
        logger ?? NullLogger.Instance);
