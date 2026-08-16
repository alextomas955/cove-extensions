using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Renamer.Options;

/// <summary>
/// Persists <see cref="RenamerOptions"/> as a single JSON blob under the <c>"options"</c> key. A thin
/// binding of the shared <see cref="ExtensionOptionsStore{TOptions}"/> to Renamer's own options model and
/// its <see cref="RenamerOptions.JsonOptions"/> (case-insensitive + enum-as-string), so the round-trip is
/// byte-for-byte what it was before the store was shared.
/// </summary>
/// <remarks>
/// The logger is optional here while the shared base requires one, and the asymmetry is deliberate: the
/// base has exactly one thing to say — that a stored blob failed to bind and the user's whole
/// configuration was replaced by defaults — and a caller who cannot hear it should have to opt out
/// rather than merely omit an argument. What opts out is a test, which has the assertion instead.
/// </remarks>
public sealed class OptionsStore(IExtensionStore store, ILogger? logger = null)
    : ExtensionOptionsStore<RenamerOptions>(
        store, RenamerOptions.JsonOptions, static () => new RenamerOptions(), logger ?? NullLogger.Instance);
