using System.Globalization;
using System.Text.Json.Nodes;

namespace WhisparrSync.Monitoring;

/// <summary>The bodies the newer generation is sent, composed rather than assembled at a call site.</summary>
/// <remarks>
/// Pure. Every flag that suppresses acquisition is set here, from ONE local, so an edit cannot set
/// one spelling and miss the other: this generation reads a top-level flag on some resources and an
/// add-options member on others, and a rule stated for one leaves the other unguarded.
/// <para>
/// The two library columns the add writes are NOT NULL with no rule set in front of them, so a
/// missing value is answered with a raw database message rather than a validation failure. Both are
/// therefore always present.
/// </para>
/// </remarks>
internal static class V3BodyProjector
{
    /// <summary>The spelling the instance was measured accepting an add-time date gate in.</summary>
    /// <remarks>
    /// It reads the same value back in a date-only spelling, so a later comparison of what was sent
    /// against what is held has to compare dates rather than strings.
    /// </remarks>
    internal const string AfterDateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Adds the studio <paramref name="foreignId"/> names, monitored at the given scope.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scope"/> is not a scope this product expresses. An unrecognised scope must
    /// never resolve to the one that marks a whole back catalogue wanted.
    /// </exception>
    internal static JsonObject AddStudio(
        string foreignId, MonitorScope scope, AddDefaults defaults, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        ArgumentNullException.ThrowIfNull(defaults);

        const bool search = false;
        var body = new JsonObject
        {
            ["foreignId"] = foreignId,
            ["rootFolderPath"] = defaults.RootFolderPath,
            ["tags"] = new JsonArray(),
            ["qualityProfileId"] = defaults.QualityProfileId,
            ["monitored"] = true,

            // A separate flag for movie-type items, needing a metadata link this product never adds.
            // Scenes are governed by the monitored flag alone.
            ["moviesMonitored"] = false,
            ["searchOnAdd"] = search,
            ["addOptions"] = new JsonObject
            {
                ["monitored"] = true,
                ["moviesMonitored"] = false,
                ["searchForMovie"] = search,
            },
        };

        switch (scope)
        {
            case MonitorScope.FutureScenes:
                body["afterDate"] = now.ToString(AfterDateFormat, CultureInfo.InvariantCulture);
                break;

            // The gate's absence IS the whole catalogue. Its own help text says an empty value is
            // ignored, so there is no value that expresses this and omission is the expression.
            case MonitorScope.AllScenes:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(scope), scope, "This is not a monitor scope this product expresses.");
        }

        return body;
    }

    /// <summary>Sets only the monitored flag on the studio <paramref name="entityId"/> names.</summary>
    /// <remarks>
    /// Every other field of the editor resource is nullable and an omitted one is not applied, so the
    /// profile, the root folder, the tags and the date gate the instance holds are all left alone.
    /// </remarks>
    internal static JsonObject SetStudioMonitored(int entityId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new JsonObject
        {
            ["studioIds"] = new JsonArray(entityId),
            ["monitored"] = monitored,
        };
    }

    /// <summary><paramref name="held"/> with the add-time date gate set to <paramref name="scope"/>.</summary>
    /// <remarks>
    /// The whole resource rather than the editor resource, because the editor resource declares no
    /// date gate at all: a scope change sent there is accepted and applies nothing.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><inheritdoc cref="AddStudio" path="/exception"/></exception>
    internal static JsonObject WithScope(JsonObject held, MonitorScope scope, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(held);

        var body = (JsonObject)held.DeepClone();
        switch (scope)
        {
            case MonitorScope.FutureScenes:
                body["afterDate"] = now.ToString(AfterDateFormat, CultureInfo.InvariantCulture);
                break;
            case MonitorScope.AllScenes:
                body.Remove("afterDate");
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(scope), scope, "This is not a monitor scope this product expresses.");
        }

        return body;
    }
}
