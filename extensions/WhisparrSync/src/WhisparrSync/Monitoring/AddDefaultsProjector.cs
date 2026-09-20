using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

internal sealed record AddDefaultsResolution(AddDefaults? Defaults, MonitorRefusalKind Refusal)
{
    internal static AddDefaultsResolution Refused(MonitorRefusalKind refusal) => new(null, refusal);

    internal static AddDefaultsResolution At(AddDefaults defaults)
        => new(defaults, MonitorRefusalKind.None);
}

// The profile is the first one the instance offered, in the order it offered them. A sort would
// make the choice depend on this code rather than on the instance.
//
// An empty or unreadable list of either refuses before anything is sent. v3 accepts a quality
// profile id of zero, echoes it back, and the entity then monitors and can never acquire anything.
// A missing library root, which a fresh instance has, makes v3's add answer a conflict carrying a
// full stack trace.
internal static class AddDefaultsProjector
{
    internal static AddDefaultsResolution From(string? qualityProfiles, string? rootFolders)
    {
        if (FirstOffered(qualityProfiles, "id") is not { } offered
            || !offered.TryGetValue<int>(out var profileId)
            || profileId < 1)
        {
            return AddDefaultsResolution.Refused(MonitorRefusalKind.NoQualityProfile);
        }

        return FirstOffered(rootFolders, "path") is { } named
            && named.TryGetValue<string>(out var rootFolder)
            && !string.IsNullOrWhiteSpace(rootFolder)
                ? AddDefaultsResolution.At(new AddDefaults(profileId, rootFolder))
                : AddDefaultsResolution.Refused(MonitorRefusalKind.NoRootFolder);
    }

    private static JsonValue? FirstOffered(string? offered, string member)
    {
        if (string.IsNullOrWhiteSpace(offered))
        {
            return null;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(offered);
        }
        catch (JsonException)
        {
            return null;
        }

        return parsed is JsonArray array && array.Count > 0 && array[0] is JsonObject first
            ? first[member] as JsonValue
            : null;
    }
}
