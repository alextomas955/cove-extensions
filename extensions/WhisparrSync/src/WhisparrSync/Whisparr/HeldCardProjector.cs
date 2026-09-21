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
    /// The rows of <paramref name="body"/> whose instance-side number is one of <paramref name="asked"/>.
    /// </summary>
    /// <remarks>
    /// One generation addresses a site by the number its metadata source issued, which its own list
    /// carries under a member named for a different source. The keys answered are the spellings the
    /// caller asked with, so a caller never has to parse a number back into its own identifier.
    /// </remarks>
    /// <returns>Null where the answer is not a list of rows at all.</returns>
    internal static IReadOnlyDictionary<string, WhisparrHeldCard>? BySiteNumber(
        string? body, IReadOnlyDictionary<int, string> asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        if (AsArray(body) is not { } rows)
        {
            return null;
        }

        var found = new Dictionary<string, WhisparrHeldCard>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is JsonObject entry
                && entry["tvdbId"] is JsonValue numbered
                && numbered.TryGetValue<int>(out var siteNumber)
                && asked.TryGetValue(siteNumber, out var asAsked))
            {
                // A site row carries no file of its own: what it holds is scenes, and a scene's own
                // row answers that. Left unestablished rather than answered false.
                found[asAsked] = new WhisparrHeldCard(Flag(entry, "monitored"), null);
            }
        }

        return found;
    }

    /// <summary>The identifiers that are a positive instance-side number, keyed by that number.</summary>
    /// <remarks>
    /// An identifier that is not one is outside what a site list can answer, so it is reported apart
    /// rather than as an absence the instance never stated.
    /// </remarks>
    internal static (IReadOnlyDictionary<int, string> Numbered, IReadOnlySet<string> Unnumbered)
        SplitBySiteNumber(IReadOnlyList<string> foreignIds)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);

        var numbered = new Dictionary<int, string>();
        var unnumbered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in foreignIds)
        {
            if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number > 0)
            {
                numbered[number] = id;
            }
            else if (!string.IsNullOrWhiteSpace(id))
            {
                unnumbered.Add(id);
            }
        }

        return (numbered, unnumbered);
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
