using System.Text.Json;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Scene;

// ReadDidNotComplete says the profile read established nothing, so the two names are absent for a
// reason that is not about the scene.
internal readonly record struct SceneProfileNames(
    string? ProfileName, string? CutoffName, bool ReadDidNotComplete);

// The cutoff is an id on the profile, not a name, and not a member of the scene at all, so it
// resolves against the profile's own items or it resolves to nothing. An id there names either one
// quality or a whole group of them.
internal static class SceneCutoffProjector
{
    // A null answer, an unsuccessful one and one that cannot be read each establish nothing. A
    // profile a readable list does not carry is an absence the list itself reported.
    internal static SceneProfileNames Project(WhisparrResponse? profiles, int? qualityProfileId)
    {
        if (profiles is null || !Parsed(profiles, out var parsed))
        {
            return new SceneProfileNames(null, null, ReadDidNotComplete: true);
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new SceneProfileNames(null, null, ReadDidNotComplete: true);
            }

            if (qualityProfileId is not { } wanted)
            {
                return default;
            }

            foreach (var profile in parsed.RootElement.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object || NumberIn(profile, "id") != wanted)
                {
                    continue;
                }

                return new SceneProfileNames(
                    TextIn(profile, "name"),
                    NumberIn(profile, "cutoff") is { } cutoff ? NameOf(profile, cutoff) : null,
                    ReadDidNotComplete: false);
            }
        }

        return default;
    }

    // Only a group carries an id of its own, so a group is matched on the item's id and a single
    // quality on its quality's. A group's leaves are walked too, because a group's members are
    // where the qualities under it are declared.
    private static string? NameOf(JsonElement profile, int cutoff)
    {
        if (!profile.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (NumberIn(item, "id") == cutoff)
            {
                return TextIn(item, "name");
            }

            if (QualityNameIn(item, cutoff) is { } named)
            {
                return named;
            }

            if (!item.TryGetProperty("items", out var nested)
                || nested.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var leaf in nested.EnumerateArray())
            {
                if (leaf.ValueKind == JsonValueKind.Object
                    && QualityNameIn(leaf, cutoff) is { } nestedName)
                {
                    return nestedName;
                }
            }
        }

        return null;
    }

    private static string? QualityNameIn(JsonElement item, int cutoff)
        => item.TryGetProperty("quality", out var quality)
            && quality.ValueKind == JsonValueKind.Object
            && NumberIn(quality, "id") == cutoff
                ? TextIn(quality, "name")
                : null;

    private static bool Parsed(WhisparrResponse answered, out JsonDocument parsed)
    {
        parsed = null!;
        if (answered.StatusCode is not (>= 200 and < 300))
        {
            return false;
        }

        try
        {
            parsed = JsonDocument.Parse(answered.Body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? NumberIn(JsonElement holder, string member)
        => holder.TryGetProperty(member, out var named)
            && named.ValueKind == JsonValueKind.Number
            && named.TryGetInt32(out var held)
                ? held
                : null;

    private static string? TextIn(JsonElement holder, string member)
        => holder.TryGetProperty(member, out var named)
            && named.ValueKind == JsonValueKind.String
            && named.GetString() is { Length: > 0 } held
                ? held
                : null;
}
