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

    /// <summary>The monitor type covering one catalogue item and nothing around it.</summary>
    /// <remarks>
    /// One of the four this generation's contract declares. The other three widen what is monitored
    /// beyond the item being registered.
    /// </remarks>
    internal const string SceneOnlyMonitorType = "sceneOnly";

    /// <summary>The add method recording that a person asked for the item.</summary>
    internal const string ManualAddMethod = "manual";

    /// <summary>This generation's search command for a studio. The one verb that downloads.</summary>
    internal const string StudiosSearchCommand = "StudiosSearch";

    /// <summary>This generation's search command for a performer. The one verb that downloads.</summary>
    internal const string PerformersSearchCommand = "PerformersSearch";

    /// <summary>This generation's catalogue-refresh command for a studio.</summary>
    internal const string RefreshStudiosCommand = "RefreshStudios";

    /// <summary>This generation's catalogue-refresh command for a performer.</summary>
    internal const string RefreshPerformersCommand = "RefreshPerformers";

    /// <summary>Adds the studio <paramref name="foreignId"/> names, monitored at the given scope.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scope"/> is not a scope this product expresses, or <paramref name="defaults"/>
    /// names no usable quality profile. An unrecognised scope must never resolve to the one that
    /// marks a whole back catalogue wanted.
    /// </exception>
    internal static JsonObject AddStudio(
        string foreignId, MonitorScope scope, AddDefaults defaults, DateTimeOffset now)
    {
        var body = Add(foreignId, defaults);

        // A studio-only flag for movie-type items, needing a metadata link this product never adds.
        // Scenes are governed by the monitored flag alone.
        body["moviesMonitored"] = false;
        ((JsonObject)body["addOptions"]!)["moviesMonitored"] = false;

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

    /// <summary>Adds the performer <paramref name="foreignId"/> names, monitored.</summary>
    /// <remarks>
    /// Takes no scope and composes no add-time date gate. That field exists on the studio resource
    /// and on no other schema this generation declares, so a future-only scope is not expressible for
    /// a performer at all: a monitored performer with no gate IS the whole catalogue, and a parameter
    /// offering the narrower scope would be a promise this member could not keep.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaults"/> names no usable quality profile.
    /// </exception>
    internal static JsonObject AddPerformer(string foreignId, AddDefaults defaults)
        => Add(foreignId, defaults);

    /// <summary>Adds the scene <paramref name="foreignId"/> names to the instance's catalogue.</summary>
    /// <remarks>
    /// The monitor type covers the scene alone: this registers one catalogue item the library holds
    /// and the instance does not, and a wider type would monitor items nobody asked about. The add
    /// method records that a person asked for it rather than a list producing it.
    /// <para>
    /// No profile is read off the parent entity. A scene the instance's own catalogue refresh creates
    /// inherits its parent's profile from the instance, so copying one here would be this product
    /// deciding something the instance owns.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaults"/> names no usable quality profile.
    /// </exception>
    internal static JsonObject AddScene(string foreignId, AddDefaults defaults)
    {
        var body = Add(foreignId, defaults);
        var addOptions = (JsonObject)body["addOptions"]!;
        addOptions["monitor"] = SceneOnlyMonitorType;
        addOptions["addMethod"] = ManualAddMethod;
        return body;
    }

    /// <summary>The command asking the instance to re-read one entity's catalogue.</summary>
    /// <remarks>
    /// An id ARRAY, which is this generation's spelling. The other generation names a single scalar
    /// id, and a body carrying the other's shape is accepted and does nothing at all.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses, or <paramref name="entityId"/>
    /// is below one.
    /// </exception>
    internal static JsonObject RefreshCatalogue(WhisparrEntityKind kind, int entityId)
        => kind switch
        {
            WhisparrEntityKind.Studio => Command(RefreshStudiosCommand, "studioIds", entityId),
            WhisparrEntityKind.Performer
                => Command(RefreshPerformersCommand, "performerIds", entityId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };

    /// <summary>The command asking the instance to look for what one entity monitors and lacks.</summary>
    /// <remarks>
    /// Composed only for a caller holding the grabbing role. It is the one body this product can
    /// compose that makes an instance acquire anything.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses, or <paramref name="entityId"/>
    /// is below one.
    /// </exception>
    internal static JsonObject SearchMonitored(WhisparrEntityKind kind, int entityId)
        => kind switch
        {
            WhisparrEntityKind.Studio => Command(StudiosSearchCommand, "studioIds", entityId),
            WhisparrEntityKind.Performer => Command(PerformersSearchCommand, "performerIds", entityId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };

    /// <summary>One command naming one entity, in this generation's id-array spelling.</summary>
    internal static JsonObject Command(string name, string idsProperty, int entityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(idsProperty);
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new JsonObject
        {
            ["name"] = name,
            [idsProperty] = new JsonArray(entityId),
        };
    }

    /// <summary>Sets only the monitored flag on the studio <paramref name="entityId"/> names.</summary>
    /// <remarks>
    /// Every other field of the editor resource is nullable and an omitted one is not applied, so the
    /// profile, the root folder, the tags and the date gate the instance holds are all left alone.
    /// Composing the flag an entity already carries yields the same body and is not an error.
    /// </remarks>
    internal static JsonObject SetStudioMonitored(int entityId, bool monitored)
        => SetMonitored("studioIds", entityId, monitored);

    /// <summary>Sets only the monitored flag on the performer <paramref name="entityId"/> names.</summary>
    /// <inheritdoc cref="SetStudioMonitored" path="/remarks"/>
    internal static JsonObject SetPerformerMonitored(int entityId, bool monitored)
        => SetMonitored("performerIds", entityId, monitored);

    /// <summary><paramref name="held"/> with the add-time date gate set to <paramref name="scope"/>.</summary>
    /// <remarks>
    /// The whole resource rather than the editor resource, because the editor resource declares no
    /// date gate at all: a scope change sent there is accepted and applies nothing.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scope"/> is not a scope this product expresses.
    /// </exception>
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

    /// <summary>What every add carries, whichever kind it names.</summary>
    /// <remarks>
    /// Both acquisition-suppressing spellings are set from one local. This generation reads the
    /// top-level flag on some resources and the add-options member on others, so a body carrying only
    /// one leaves the other unguarded.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaults"/> names no usable quality profile. This generation accepts a zero
    /// profile id, echoes it back, and the entity then monitors and can never acquire anything.
    /// </exception>
    private static JsonObject Add(string foreignId, AddDefaults defaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaults.RootFolderPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaults.QualityProfileId, 1);

        const bool search = false;
        return new JsonObject
        {
            ["foreignId"] = foreignId,
            ["rootFolderPath"] = defaults.RootFolderPath,
            ["tags"] = new JsonArray(),
            ["qualityProfileId"] = defaults.QualityProfileId,
            ["monitored"] = true,
            ["searchOnAdd"] = search,
            ["addOptions"] = new JsonObject
            {
                ["monitored"] = true,
                ["searchForMovie"] = search,
            },
        };
    }

    private static JsonObject SetMonitored(string idsProperty, int entityId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new JsonObject
        {
            [idsProperty] = new JsonArray(entityId),
            ["monitored"] = monitored,
        };
    }
}
