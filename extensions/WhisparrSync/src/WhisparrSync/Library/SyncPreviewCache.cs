using System.Collections.Concurrent;
using WhisparrSync.Contracts;

namespace WhisparrSync.Library;

/// <summary>
/// Holds what the last count found for each generation, so a reader who leaves the page and comes
/// back reaches the result they already paid for.
/// </summary>
// A singleton, bounded by construction: one entry per generation, each three integers and an
// instant. Nothing per scene joins it, and nothing here is written to IExtensionStore, where a
// per-scene value would be serialized into the host's bulk data route and break the settings page.
// The entry expires on its own rather than being invalidated by a writer. This extension is never
// told what the instance holds, so a reading with no expiry could stay wrong until the host
// restarted.
internal sealed class SyncPreviewCache(TimeProvider clock)
{
    // A count older than this is not answered, so no stale count is shown without its age.
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<
        WhisparrGeneration, (DateTimeOffset HeldAt, SyncPreviewView Counts)> _entries = new();

    internal SyncPreviewView? Held(WhisparrGeneration generation)
        => _entries.TryGetValue(generation, out var entry)
            && clock.GetUtcNow() - entry.HeldAt < Lifetime
                ? entry.Counts
                : null;

    internal void Hold(WhisparrGeneration generation, SyncPreviewView counts)
        => _entries[generation] = (clock.GetUtcNow(), counts);
}
