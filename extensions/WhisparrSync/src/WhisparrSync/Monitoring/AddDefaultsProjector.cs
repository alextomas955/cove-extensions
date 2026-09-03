using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;

namespace WhisparrSync.Monitoring;

/// <summary>The values an add was composed from, or why it could not be composed.</summary>
/// <param name="Defaults">The values to compose with, or null on a refusal.</param>
/// <param name="Refusal">Why there are none, or <see cref="MonitorRefusalKind.None"/>.</param>
internal sealed record AddDefaultsResolution(AddDefaults? Defaults, MonitorRefusalKind Refusal)
{
    internal static AddDefaultsResolution Refused(MonitorRefusalKind refusal) => new(null, refusal);

    internal static AddDefaultsResolution At(AddDefaults defaults)
        => new(defaults, MonitorRefusalKind.None);
}

/// <summary>What an entity is added with, read from the instance rather than assumed.</summary>
/// <remarks>
/// Pure. Both values are the instance's own and neither is this product's to choose: the profile is
/// the first the instance offered, in the order it offered them, with no sort and no re-ordering. A
/// sort would make the choice depend on this code rather than on the instance, and what is wanted is
/// the one the instance offers first.
/// <para>
/// An empty list of either is a stop taken BEFORE anything is sent. That is not belt and braces: the
/// newer generation accepts a quality profile id of zero, echoes it back, and the entity then
/// monitors happily and can never acquire anything, so refusing here is the only guard there is.
/// The same holds for a missing library root, which a fresh instance has: that generation's add then
/// answers a conflict carrying a full stack trace, and a user's first monitor would show it.
/// </para>
/// <para>
/// One profile is taken and no second decision follows it. A scene a catalogue refresh creates
/// inherits its own entity's profile, so where that profile is also the one offered first there is
/// still a single choice and a branch for the coincidence would be a branch for nothing.
/// </para>
/// </remarks>
internal static class AddDefaultsProjector
{
    /// <summary>
    /// The values to add with, from what <paramref name="qualityProfiles"/> and
    /// <paramref name="rootFolders"/> answered.
    /// </summary>
    /// <remarks>
    /// A body that is not a readable array of objects is treated as offering nothing, which is the
    /// same stop as an empty one: neither yields a value that could be sent.
    /// </remarks>
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

    /// <summary><paramref name="member"/> of the first element of <paramref name="offered"/>.</summary>
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
