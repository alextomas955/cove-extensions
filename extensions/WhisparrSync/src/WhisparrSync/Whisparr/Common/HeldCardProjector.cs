using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisparrSync.Whisparr;

/// <summary>
/// Reduces one list answer to the cards the caller asked about.
/// </summary>
/// <remarks>
/// Every reduction is keyed by the caller's own identifiers, so a whole catalogue reduces to at most
/// as many entries as were asked about and nothing here grows with the instance's holdings.
/// </remarks>
internal static class HeldCardProjector
{
    /// <summary>
    /// The rows of <paramref name="body"/> whose foreign identifier is one of <paramref name="asked"/>.
    /// </summary>
    /// <remarks>
    /// Matched without regard to case: an identifier is a hexadecimal uuid and each side stored its
    /// own spelling. A row is read off its stash id, falling back to its foreign id, because both
    /// carry the same uuid and which one an instance fills in varies by resource.
    /// </remarks>
    /// <returns>Null where the answer is not a list of rows at all.</returns>
    internal static IReadOnlyDictionary<string, WhisparrHeldCard>? ByForeignId(
        string? body, IReadOnlyCollection<string> asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        if (AsArray(body) is not { } rows)
        {
            return null;
        }

        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in asked)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                wanted[id] = id;
            }
        }

        var found = new Dictionary<string, WhisparrHeldCard>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is not JsonObject entry)
            {
                continue;
            }

            var named = Text(entry, "stashId") ?? Text(entry, "foreignId");
            if (named is { Length: > 0 } spelled && wanted.TryGetValue(spelled, out var asAsked))
            {
                found[asAsked] = new WhisparrHeldCard(Flag(entry, "monitored"), Flag(entry, "hasFile"));
            }
        }

        return found;
    }

    /// <summary>
    /// What the instance holds for the one site a lookup answered, or null where it holds none.
    /// </summary>
    /// <remarks>
    /// The lookup answers the instance's own row for a site it holds, so a positive id is the
    /// instance stating presence. A site it does not hold is answered from the metadata source and
    /// carries no id, which is an absence rather than a refusal.
    /// <para>
    /// Ordered by the lookup's own relevance and asked by an exact identifier, so the first entry is
    /// the site asked about.
    /// </para>
    /// </remarks>
    internal static SiteLookupReading FromSiteLookup(string? body)
    {
        if (AsArray(body) is not { } rows)
        {
            // Not a list of sites at all. Read as an absence it would report a site the instance
            // never spoke about as one it holds none of.
            return SiteLookupReading.Unreadable;
        }

        if (rows.Count == 0
            || rows[0] is not JsonObject site
            || site["id"] is not JsonValue numbered
            || !numbered.TryGetValue<int>(out var rowId)
            || rowId <= 0)
        {
            return SiteLookupReading.HoldsNone;
        }

        // A site row carries no file of its own: what it holds is scenes, and a scene's own row
        // answers that. Left unestablished rather than answered false.
        return SiteLookupReading.Holding(new WhisparrHeldCard(Flag(site, "monitored"), null));
    }

    /// <summary>The term the site lookup is asked with for <paramref name="foreignId"/>.</summary>
    /// <remarks>
    /// A number the metadata source issued is asked for under the prefix the lookup reads as that
    /// source's own id, which it answers from its own rows without reaching the source at all. Any
    /// other spelling is passed through as the search term it is.
    /// </remarks>
    internal static string SiteLookupTerm(string foreignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        return int.TryParse(foreignId, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0
                ? $"tpdb:{foreignId}"
                : foreignId;
    }

    private static JsonArray? AsArray(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject row, string field)
        => row[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? Flag(JsonObject row, string field)
        => row[field] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
