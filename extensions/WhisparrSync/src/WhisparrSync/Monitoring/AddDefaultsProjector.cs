using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
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
    /// <summary>
    /// The declared root a file under <paramref name="instanceFolder"/> can be linked into.
    /// </summary>
    /// <remarks>
    /// The root sharing the longest leading path with the folder. A hard link cannot cross a
    /// filesystem, and on an instance reaching several of the library's volumes each is mounted
    /// under its own leading segment, so the root sharing that segment is the one on the same
    /// filesystem. A root sharing nothing answers null, and a caller registers nothing there rather
    /// than registering where a link would copy the file instead.
    /// </remarks>
    internal static string? RootReachingFrom(
        string instanceFolder, IReadOnlyList<string> instanceRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceFolder);
        ArgumentNullException.ThrowIfNull(instanceRoots);

        string? nearest = null;
        var shared = 0;
        foreach (var root in instanceRoots)
        {
            var common = SharedSegments(instanceFolder, root);
            if (common > shared)
            {
                shared = common;
                nearest = root;
            }
        }

        return shared == 0 ? null : nearest;
    }

    // Leading path segments the two have in common, compared the way the instance spells its own
    // filesystem.
    private static int SharedSegments(string folder, string root)
    {
        var left = PathCandidateGuard.Normalize(folder)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var right = PathCandidateGuard.Normalize(root)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        var shared = 0;
        while (shared < left.Length
            && shared < right.Length
            && string.Equals(left[shared], right[shared], StringComparison.OrdinalIgnoreCase))
        {
            shared++;
        }

        return shared;
    }

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
