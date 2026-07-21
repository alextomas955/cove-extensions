using Cove.Extensions.Shared;
using Cove.Plugins;

namespace WhisparrSync.Options;

/// <summary>
/// Persists <see cref="WhisparrOptions"/> as a single JSON blob under the <c>"options"</c> key. A thin
/// binding of the shared <see cref="ExtensionOptionsStore{TOptions}"/> to WhisparrSync's own options model
/// and its <see cref="WhisparrOptions.JsonOptions"/> (case-insensitive), so the round-trip is byte-for-byte
/// what it was before the store was shared.
/// </summary>
public sealed class OptionsStore(IExtensionStore store)
    : ExtensionOptionsStore<WhisparrOptions>(store, WhisparrOptions.JsonOptions, static () => new WhisparrOptions());
