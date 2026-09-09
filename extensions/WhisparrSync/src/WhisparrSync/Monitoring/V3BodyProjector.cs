using System.Globalization;
using System.Text.Json.Nodes;
using Whisparr3.Net.Client;
using Whisparr3.Net.Model;

namespace WhisparrSync.Monitoring;

/// <summary>The bodies the newer generation is sent, composed rather than assembled at a call site.</summary>
/// <remarks>
/// Pure. Every flag that suppresses acquisition is set here, from ONE constant, so an edit cannot
/// set one and miss another. Each resource an add can name declares exactly one such flag and they
/// differ: the studio and performer resources declare a top-level one, the scene resource declares
/// one inside its add-options member and no top-level one. A flag a resource does not declare is
/// discarded by the instance, so sending it would report a suppression that was never applied.
/// <para>
/// The two library columns the add writes are NOT NULL with no rule set in front of them, so a
/// missing value is answered with a raw database message rather than a validation failure. Both are
/// therefore always present.
/// </para>
/// </remarks>
internal static class V3BodyProjector
{
    /// <summary>The value of every flag that stops an add acquiring anything.</summary>
    internal const bool NoAcquisition = false;

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
    internal const MonitorTypes SceneOnlyMonitorType = MonitorTypes.SceneOnly;

    /// <summary>The add method recording that a person asked for the item.</summary>
    internal const AddMovieMethod ManualAddMethod = AddMovieMethod.Manual;

    /// <summary>This generation's search command for a studio. The one verb that downloads.</summary>
    internal const string StudiosSearchCommand = "StudiosSearch";

    /// <summary>This generation's search command for a performer. The one verb that downloads.</summary>
    internal const string PerformersSearchCommand = "PerformersSearch";

    /// <summary>This generation's search command for one scene. The one verb that downloads.</summary>
    /// <remarks>
    /// The spelling the instance was measured accepting, together with the id-array member it honours.
    /// A name this generation does not register is refused outright, and an id member it does not
    /// declare is dropped and the command then runs over nothing.
    /// </remarks>
    internal const string ScenesSearchCommand = "MoviesSearch";

    /// <summary>The id-array member the per-scene search names its scene in.</summary>
    /// <inheritdoc cref="ScenesSearchCommand" path="/remarks"/>
    internal const string SceneIdsProperty = "movieIds";

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
    internal static StudioResource AddStudio(
        string foreignId, MonitorScope scope, AddDefaults defaults, DateTimeOffset now)
    {
        Require(foreignId, defaults);

        return new StudioResource(
            foreignId: foreignId,
            rootFolderPath: defaults.RootFolderPath,
            qualityProfileId: defaults.QualityProfileId,
            tags: new List<int>(),
            monitored: true,
            searchOnAdd: NoAcquisition,

            // A studio-only flag for movie-type items, needing a metadata link this product never
            // adds. Scenes are governed by the monitored flag alone.
            moviesMonitored: false,
            afterDate: AfterDateFor(scope, now));
    }

    /// <summary>The add-time date gate expressing <paramref name="scope"/>, or none.</summary>
    /// <remarks>
    /// The gate's absence IS the whole catalogue. Its own help text says an empty value is ignored,
    /// so there is no value that expresses this and omission is the expression.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scope"/> is not a scope this product expresses.
    /// </exception>
    private static Option<string?> AfterDateFor(MonitorScope scope, DateTimeOffset now)
        => scope switch
        {
            MonitorScope.FutureScenes =>
                new Option<string?>(now.ToString(AfterDateFormat, CultureInfo.InvariantCulture)),
            MonitorScope.AllScenes => default,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope), scope, "This is not a monitor scope this product expresses."),
        };

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
    internal static PerformerResource AddPerformer(string foreignId, AddDefaults defaults)
    {
        Require(foreignId, defaults);

        return new PerformerResource(
            foreignId: foreignId,
            rootFolderPath: defaults.RootFolderPath,
            qualityProfileId: defaults.QualityProfileId,
            tags: new List<int>(),
            monitored: true,
            searchOnAdd: NoAcquisition);
    }

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
    /// <para>
    /// The title member is the IDENTIFIER, and it is there because a scene add carrying no title is
    /// refused outright. The instance validates the member for emptiness and then replaces it with
    /// what its own metadata provider answers, so no value sent here survives and the identifier is
    /// the one value that is true of the request. An identifier the provider cannot resolve is
    /// refused rather than stored under whatever was sent, so there is no answer in which a reader
    /// sees this. Both halves are pinned.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaults"/> names no usable quality profile.
    /// </exception>
    internal static MovieResource AddScene(string foreignId, AddDefaults defaults)
    {
        Require(foreignId, defaults);

        return new MovieResource(
            foreignId: foreignId,
            title: foreignId,
            rootFolderPath: defaults.RootFolderPath,
            qualityProfileId: defaults.QualityProfileId,
            tags: new List<int>(),
            monitored: true,
            addOptions: new AddMovieOptions(
                monitor: SceneOnlyMonitorType,
                addMethod: ManualAddMethod,
                searchForMovie: NoAcquisition));
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

    /// <summary>The command asking the instance to look for one scene it holds.</summary>
    /// <remarks>
    /// Composed only for a caller holding the per-scene grabbing role, and it is one of the two
    /// bodies this product can compose that make an instance acquire anything.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sceneId"/> is below one.
    /// </exception>
    internal static JsonObject SearchScene(int sceneId)
        => Command(ScenesSearchCommand, SceneIdsProperty, sceneId);

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
    internal static StudioEditorResource SetStudioMonitored(int entityId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new StudioEditorResource(studioIds: new List<int> { entityId }, monitored: monitored);
    }

    /// <summary>Sets only the monitored flag on one scene.</summary>
    /// <remarks>
    /// The patch resource declares the flag and no other member at all, so the profile, the root
    /// folder, the tags and the file the instance holds for that scene are left alone. The scene is
    /// named by the route's own segment, which is why no identifier is composed here.
    /// </remarks>
    internal static MoviePatchResource SceneMonitorPatch(bool monitored)
        => new(monitored: monitored);

    /// <summary>Excludes the scene <paramref name="foreignId"/> names.</summary>
    /// <remarks>
    /// The instance validates the exclusion's display title as non-empty and binds it as
    /// <c>movieTitle</c>, so a body carrying the identifier alone, or carrying a plain <c>title</c>,
    /// is refused before anything is written. The title is composed from the identifier because an
    /// exclusion governs a later catalogue addition and is offered for a scene the instance holds no
    /// row to take a name from. The instance keeps what is sent here, so this is the name its own
    /// exclusion list reads under. The type is stated rather than left to the instance's default,
    /// because the default is the instance's to change and this product excludes scenes alone. The
    /// reason is the instance's own to fill, and it declares no acquisition-suppressing member on
    /// this resource at all, so an exclusion issues no search.
    /// </remarks>
    internal static ImportListExclusionResource SceneExclusion(string foreignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        return new ImportListExclusionResource(
            foreignId: foreignId,
            movieTitle: SceneExclusionTitle(foreignId),
            type: SceneExclusionType);
    }

    /// <summary>The exclusion type covering one scene, which is the only kind this product excludes.</summary>
    internal const ImportExclusionType SceneExclusionType = ImportExclusionType.Scene;

    /// <summary>The display title an exclusion of the scene <paramref name="foreignId"/> names carries.</summary>
    internal static string SceneExclusionTitle(string foreignId) => $"Scene {foreignId}";

    /// <summary>Sets only the monitored flag on the performer <paramref name="entityId"/> names.</summary>
    /// <inheritdoc cref="SetStudioMonitored" path="/remarks"/>
    internal static PerformerEditorResource SetPerformerMonitored(int entityId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new PerformerEditorResource(performerIds: new List<int> { entityId }, monitored: monitored);
    }

    /// <summary><paramref name="held"/> with the add-time date gate set to <paramref name="scope"/>.</summary>
    /// <remarks>
    /// The whole resource rather than the editor resource, because the editor resource declares no
    /// date gate at all: a scope change sent there is accepted and applies nothing.
    /// <para>
    /// The only body this product composes by cloning a whole instance response, so it carries members
    /// nothing here wrote. The read echoes the top-level acquisition-suppressing flag, and an entity a
    /// person added in the instance's own interface with search-on-add ticked holds it TRUE: a clone
    /// sent back as-is re-asserts a user's search flag on a request this product originated. It is
    /// therefore overwritten every time. OVERWRITTEN rather than removed, because omission relies on
    /// the instance defaulting an absent member to false, which was never measured.
    /// </para>
    /// <para>
    /// The add-options member is overwritten only where the clone already carries one. This resource's
    /// own schema declares no such member and the read never answers with one, so composing it here
    /// would send a shape the instance was never measured accepting on this route. A member the
    /// instance did not answer with cannot ride back out.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scope"/> is not a scope this product expresses.
    /// </exception>
    internal static JsonObject WithScope(JsonObject held, MonitorScope scope, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(held);

        var body = (JsonObject)held.DeepClone();
        const bool search = false;
        body["searchOnAdd"] = search;
        if (body["addOptions"] is JsonObject addOptions)
        {
            addOptions["searchForMovie"] = search;
        }

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

    /// <summary>What every add requires of its caller, whichever kind it names.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaults"/> names no usable quality profile. This generation accepts a zero
    /// profile id, echoes it back, and the entity then monitors and can never acquire anything.
    /// </exception>
    private static void Require(string foreignId, AddDefaults defaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaults.RootFolderPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaults.QualityProfileId, 1);
    }
}
