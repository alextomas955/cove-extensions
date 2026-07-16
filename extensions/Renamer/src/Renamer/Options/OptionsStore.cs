using Cove.Extensions.Shared;
using Cove.Plugins;

namespace Renamer.Options;

/// <summary>
/// Persists <see cref="RenamerOptions"/> as a single JSON blob under the <c>"options"</c> key. A thin
/// binding of the shared <see cref="ExtensionOptionsStore{TOptions}"/> to Renamer's own options model and
/// its <see cref="RenamerOptions.JsonOptions"/> (case-insensitive + enum-as-string), so the round-trip is
/// byte-for-byte what it was before the store was shared.
/// </summary>
public sealed class OptionsStore(IExtensionStore store)
    : ExtensionOptionsStore<RenamerOptions>(store, RenamerOptions.JsonOptions, static () => new RenamerOptions());
