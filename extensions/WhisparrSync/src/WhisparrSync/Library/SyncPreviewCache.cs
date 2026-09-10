using System.Collections.Concurrent;
using WhisparrSync.Contracts;

namespace WhisparrSync.Library;

/// <summary>
/// Holds what the last count found for each generation, so a reader who leaves the page and comes
/// back reaches the result they already paid for.
/// </summary>
/// <remarks>
/// A singleton, and bounded by construction: one entry per generation, each three integers and an
/// instant. Nothing per scene joins it, and nothing here is written to <c>IExtensionStore</c> - a
/// stored per-scene value would be serialized into the host's bulk data route and would break the
/// whole settings page.
/// <para>
/// The entry expires on its own rather than being invalidated by a writer. What the instance holds
/// is a change this extension is never told about, so a reading with no expiry would be a reading
/// that could stay wrong until the host restarted.
/// </para>
/// <para>
/// The held value is the wire projection itself rather than a second record of the same three
/// numbers. Two records would be two places for the count set to change in, and the read route
/// answers this value unaltered.
/// </para>
/// </remarks>
internal sealed class SyncPreviewCache(TimeProvider clock)
{
    /// <summary>How long a count is answered before it can no longer be read at all.</summary>
    /// <remarks>
    /// Shorter than a day, so the age the page renders beside the counts is always relative. A
    /// figure older than this is not answered, so no stale count is ever shown without its age.
    /// </remarks>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<
        WhisparrGeneration, (DateTimeOffset HeldAt, SyncPreviewView Counts)> _entries = new();

    /// <summary>The held counts for <paramref name="generation"/>, or null when none are in date.</summary>
    internal SyncPreviewView? Held(WhisparrGeneration generation)
        => _entries.TryGetValue(generation, out var entry)
            && clock.GetUtcNow() - entry.HeldAt < Lifetime
                ? entry.Counts
                : null;

    /// <summary>Holds <paramref name="counts"/> as <paramref name="generation"/>'s current count.</summary>
    internal void Hold(WhisparrGeneration generation, SyncPreviewView counts)
        => _entries[generation] = (clock.GetUtcNow(), counts);
}
