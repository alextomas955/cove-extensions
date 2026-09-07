using System.Text.Json;

namespace WhisparrSync.Providers;

/// <summary>Turns one provider's scene row into this product's own scene.</summary>
/// <remarks>
/// Pure. A member the provider gave no value for stays null, so a surface omits that row rather than
/// rendering an empty one. The two providers share no field spelling, so each has its own reader and
/// both reach the same record.
/// </remarks>
internal static class ProviderSceneProjector
{
    /// <summary>The scene <paramref name="row"/> describes, or null where it names no identifier.</summary>
    /// <remarks>
    /// A row with no identifier is dropped: every verb a card offers names the scene by it, so a card
    /// without one carries no action that could be composed.
    /// </remarks>
    internal static ProviderScene? Project(JsonElement row)
    {
        var id = Text(row, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new ProviderScene(
            id,
            Text(row, "title") ?? string.Empty,
            Text(row, "release_date"),
            FirstImageUrl(row),
            NestedText(row, "studio", "name"),
            Text(row, "details"),
            Performers(row),
            Tags(row));
    }

    /// <summary>The scene <paramref name="row"/> describes, spelled as ThePornDB spells it.</summary>
    /// <remarks>
    /// The performer named on a scene is that site's own credit rather than the canonical record, so
    /// the name and picture are read from it and the scene reads as the site published it.
    /// </remarks>
    internal static ProviderScene? ProjectThePornDbScene(JsonElement row)
    {
        var id = Text(row, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new ProviderScene(
            id,
            Text(row, "title") ?? string.Empty,
            Text(row, "date"),
            Text(row, "image") ?? Text(row, "poster"),
            NestedText(row, "site", "name"),
            Text(row, "description"),
            ThePornDbPerformers(row),
            Tags(row));
    }

    private static List<ProviderPerformer> ThePornDbPerformers(JsonElement row)
    {
        if (!row.TryGetProperty("performers", out var credited)
            || credited.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var performers = new List<ProviderPerformer>();
        foreach (var performer in credited.EnumerateArray())
        {
            var id = Text(performer, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            performers.Add(
                new ProviderPerformer(
                    id,
                    Text(performer, "name") ?? string.Empty,
                    Text(performer, "face")
                        ?? Text(performer, "thumbnail")
                        ?? Text(performer, "image")));
        }

        return performers;
    }

    private static List<ProviderPerformer> Performers(JsonElement row)
    {
        if (!row.TryGetProperty("performers", out var appearances)
            || appearances.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var performers = new List<ProviderPerformer>();
        foreach (var appearance in appearances.EnumerateArray())
        {
            if (!appearance.TryGetProperty("performer", out var performer)
                || performer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Text(performer, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            performers.Add(
                new ProviderPerformer(
                    id, Text(performer, "name") ?? string.Empty, FirstImageUrl(performer)));
        }

        return performers;
    }

    private static IReadOnlyList<string> Tags(JsonElement row)
    {
        if (!row.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. tags.EnumerateArray()
                .Select(tag => Text(tag, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!),
        ];
    }

    private static string? FirstImageUrl(JsonElement row)
    {
        if (!row.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var image in images.EnumerateArray())
        {
            if (Text(image, "url") is { Length: > 0 } url)
            {
                return url;
            }
        }

        return null;
    }

    private static string? NestedText(JsonElement row, string property, string nested)
        => row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? Text(value, nested)
            : null;

    private static string? Text(JsonElement row, string property)
        => row.ValueKind == JsonValueKind.Object
            && row.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}
