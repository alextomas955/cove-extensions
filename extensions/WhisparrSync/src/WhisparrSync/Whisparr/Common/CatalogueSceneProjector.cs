using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisparrSync.Whisparr;

/// <summary>
/// Reads an entity's scene rows out of the answer each generation lists them in.
/// </summary>
/// <remarks>
/// The two generations name a scene, its date and its performers under different members, and one
/// nests the cover under an image list. Both are read here so the missing surface sees one shape.
/// </remarks>
internal static class CatalogueSceneProjector
{
    /// <summary>The rows of a v3 works answer, which lists one movie resource per scene.</summary>
    /// <returns>Null where the answer is not a list of rows at all.</returns>
    internal static IReadOnlyList<WhisparrCatalogueScene>? V3Works(string? body)
    {
        if (AsArray(body) is not { } rows)
        {
            return null;
        }

        var scenes = new List<WhisparrCatalogueScene>(rows.Count);
        foreach (var row in rows)
        {
            if (row is not JsonObject entry)
            {
                continue;
            }

            // The stash id and the foreign id carry the same uuid, and which one a build fills in
            // varies, so a row missing both names no scene and is left out.
            var named = Text(entry, "stashId") ?? Text(entry, "foreignId");
            if (named is not { Length: > 0 } sceneId)
            {
                continue;
            }

            scenes.Add(new WhisparrCatalogueScene(
                sceneId,
                Text(entry, "title") ?? sceneId,
                DateOnlyIn(Text(entry, "releaseDate") ?? Text(entry, "digitalRelease")),
                CoverIn(entry["images"]),
                Text(entry, "studioTitle"),
                Text(entry, "overview"),
                V3Performers(entry),
                Strings(entry["genres"]),
                Flag(entry, "monitored") ?? false,
                Flag(entry, "hasFile") ?? false,

                // The instance's own row id, where it holds one. A works listing carries a catalogue
                // item the instance may hold no entry for, and such a row carries none.
                Number(entry, "id")));
        }

        return scenes;
    }

    /// <summary>The rows of a v2 episode answer, which lists one episode per scene under a site.</summary>
    /// <returns>Null where the answer is not a list of rows at all.</returns>
    internal static IReadOnlyList<WhisparrCatalogueScene>? V2Episodes(string? body, string? siteName)
    {
        if (AsArray(body) is not { } rows)
        {
            return null;
        }

        var scenes = new List<WhisparrCatalogueScene>(rows.Count);
        foreach (var row in rows)
        {
            // A scene is addressed by the number the metadata source issued, which this generation
            // carries under a member named for a different source.
            if (row is not JsonObject entry
                || entry["tvdbId"] is not JsonValue numbered
                || !numbered.TryGetValue<int>(out var sceneNumber)
                || sceneNumber < 1)
            {
                continue;
            }

            scenes.Add(new WhisparrCatalogueScene(
                sceneNumber.ToString(CultureInfo.InvariantCulture),
                Text(entry, "title") ?? sceneNumber.ToString(CultureInfo.InvariantCulture),
                DateOnlyIn(Text(entry, "releaseDate") ?? Text(entry, "airDate")),
                CoverIn(entry["images"]),
                siteName,
                Text(entry, "overview"),
                V2Actors(entry),
                [],
                Flag(entry, "monitored") ?? false,
                Flag(entry, "hasFile") ?? false,

                // The instance's own row id. This generation lists a scene it already holds a row
                // for, so marking one is a flip of that row rather than an add.
                Number(entry, "id")));
        }

        return scenes;
    }

    private static int Number(JsonObject row, string field)
        => row[field] is JsonValue value && value.TryGetValue<int>(out var number) && number > 0
            ? number
            : 0;

    private static List<WhisparrCataloguePerformer> V3Performers(JsonObject entry)
    {
        // Two parallel lists rather than one list of objects, so a row whose lengths disagree is read
        // as far as both reach.
        var ids = Strings(entry["performerForeignIds"]);
        var names = Strings(entry["performerNames"]);
        var performers = new List<WhisparrCataloguePerformer>(Math.Min(ids.Count, names.Count));
        for (var at = 0; at < ids.Count && at < names.Count; at++)
        {
            performers.Add(new WhisparrCataloguePerformer(ids[at], names[at], null));
        }

        return performers;
    }

    private static List<WhisparrCataloguePerformer> V2Actors(JsonObject entry)
    {
        if (entry["actors"] is not JsonArray actors)
        {
            return [];
        }

        var performers = new List<WhisparrCataloguePerformer>(actors.Count);
        foreach (var actor in actors)
        {
            if (actor is not JsonObject named || Text(named, "name") is not { Length: > 0 } name)
            {
                continue;
            }

            var id = named["tpdbId"] is JsonValue numbered && numbered.TryGetValue<int>(out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : name;
            performers.Add(new WhisparrCataloguePerformer(id, name, CoverIn(named["images"])));
        }

        return performers;
    }

    // The first image the row carries, whatever it is a cover of: a row that names its cover type
    // and one that does not are both answered here, and a scene with no image reads as none.
    private static string? CoverIn(JsonNode? images)
    {
        if (images is not JsonArray listed)
        {
            return null;
        }

        foreach (var image in listed)
        {
            if (image is JsonObject entry
                && (Text(entry, "remoteUrl") ?? Text(entry, "url")) is { Length: > 0 } address)
            {
                return address;
            }
        }

        return null;
    }

    // Kept as the date alone: one generation answers a date and the other a timestamp, and the two
    // sort together only where the time is dropped.
    private static string? DateOnlyIn(string? value)
        => value is { Length: >= 10 } dated ? dated[..10] : value;

    private static List<string> Strings(JsonNode? node)
    {
        if (node is not JsonArray listed)
        {
            return [];
        }

        var values = new List<string>(listed.Count);
        foreach (var item in listed)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text)
                && text is { Length: > 0 })
            {
                values.Add(text);
            }
        }

        return values;
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
