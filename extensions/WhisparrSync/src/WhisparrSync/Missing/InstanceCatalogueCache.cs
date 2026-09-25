using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Missing;

/// <summary>
/// Holds the catalogue last read from the instance, so the count beside a tab, the page under it and
/// a page turn are one read rather than three.
/// </summary>
// One slot, so what is held is bounded by a single entity's catalogue and not by how many entities a
// reader visits. Nothing per scene is written to IExtensionStore, where a per-scene value would be
// serialized into the host's bulk data route.
//
// The entry expires on its own rather than being invalidated by a writer: this extension is never
// told what the instance holds, so an entry with no expiry could stay wrong until the host restarted.
internal sealed class InstanceCatalogueCache(TimeProvider clock)
{
    // Long enough to cover one reader's page turns, short enough that a scene added in Whisparr
    // shows up on a deliberate refresh.
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();

    private (WhisparrGeneration Generation, WhisparrEntityKind Kind, string ForeignId,
        DateTimeOffset HeldAt, IReadOnlyList<WhisparrCatalogueScene> Scenes)? _held;

    internal IReadOnlyList<WhisparrCatalogueScene>? Held(
        WhisparrGeneration generation, WhisparrEntityKind kind, string foreignId)
    {
        lock (_gate)
        {
            return _held is { } entry
                && entry.Generation == generation
                && entry.Kind == kind
                && string.Equals(entry.ForeignId, foreignId, StringComparison.Ordinal)
                && clock.GetUtcNow() - entry.HeldAt < Lifetime
                    ? entry.Scenes
                    : null;
        }
    }

    internal void Hold(
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        string foreignId,
        IReadOnlyList<WhisparrCatalogueScene> scenes)
    {
        lock (_gate)
        {
            _held = (generation, kind, foreignId, clock.GetUtcNow(), scenes);
        }
    }

    /// <summary>Drops what is held, so the next read reaches the instance.</summary>
    internal void Forget()
    {
        lock (_gate)
        {
            _held = null;
        }
    }
}
