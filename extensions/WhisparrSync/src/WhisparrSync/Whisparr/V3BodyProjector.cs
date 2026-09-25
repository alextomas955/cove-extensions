using System.Globalization;
using System.Text.Json.Nodes;
using Whisparr3.Net.Client;
using Whisparr3.Net.Model;
using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

// Every acquisition-suppressing flag is set here from one constant, so an edit cannot set one and
// miss another. Each add resource declares exactly one and they differ: studio and performer a
// top-level flag, the scene resource one inside add-options and none at the top. A flag a resource
// does not declare is discarded, so sending it reports a suppression never applied.
//
// The two library columns the add writes are NOT NULL with no rule in front of them: a missing
// value answers a raw database message, not a validation failure. Both are always present.
internal static class V3BodyProjector
{
    internal const bool NoAcquisition = false;

    // The spelling the instance was measured accepting an add-time date gate in. It reads the same
    // value back in a date-only spelling, so a later comparison compares dates, not strings.
    internal const string AfterDateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    // Covers one catalogue item. The other three types this generation declares widen what is
    // monitored beyond the item being registered.
    internal const MonitorTypes SceneOnlyMonitorType = MonitorTypes.SceneOnly;

    internal const AddMovieMethod ManualAddMethod = AddMovieMethod.Manual;

    // The three search commands are the verbs that download.
    internal const string StudiosSearchCommand = "StudiosSearch";

    internal const string PerformersSearchCommand = "PerformersSearch";

    // The spelling the instance was measured accepting, together with the id-array member below
    // that it honours. A name this generation does not register is refused outright, and an id
    // member it does not declare is dropped, leaving the command to run over nothing.
    internal const string ScenesSearchCommand = "MoviesSearch";

    internal const string SceneIdsProperty = "movieIds";

    internal const string RefreshStudiosCommand = "RefreshStudios";

    // The quality and the language the instance answers a reading with, and the only two values it
    // treats as asking rather than stating.
    internal const int UnknownQualityId = 0;

    internal const int UnknownLanguageId = 0;

    internal const string RefreshPerformersCommand = "RefreshPerformers";

    // Asks the instance to read one file, by handing it the quality and the languages unstated. The
    // instance fills a member that is present and unknown and keeps one that is stated, so sending
    // the unknown members is what makes it read rather than accept. The route takes a list and is
    // handed one entry.
    internal static List<ManualImportReprocessResource> ReadOwnedFile(OwnedFilePlacement file)
        => [
            new ManualImportReprocessResource(
                id: 0,
                path: file.Path,
                movieId: file.EntityId,
                quality: new QualityModel(
                    quality: new Quality(id: UnknownQualityId),
                    revision: new Revision(varVersion: 1, real: 0, isRepack: false)),
                languages: new List<Language> { new(id: UnknownLanguageId) },
                releaseGroup: string.Empty,
                downloadId: string.Empty,
                indexerFlags: 0),
        ];

    // Throws on an unexpressed scope, which must never resolve to the one that marks a whole back
    // catalogue wanted.
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

    // The gate's absence is the whole catalogue. Its help text says an empty value is ignored, so
    // omission is the only expression of it.
    private static Option<string?> AfterDateFor(MonitorScope scope, DateTimeOffset now)
        => scope switch
        {
            MonitorScope.FutureScenes =>
                new Option<string?>(now.ToString(AfterDateFormat, CultureInfo.InvariantCulture)),
            MonitorScope.AllScenes => default,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope), scope, "This is not a monitor scope this product expresses."),
        };

    // Takes no scope. The date gate exists on the studio resource and on no other schema this
    // generation declares, so a future-only scope is not expressible for a performer: a monitored
    // performer with no gate is the whole catalogue.
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

    // Tracks an entity's catalogue and wants none of it.
    //
    // Hand-composed rather than built from the generated resource: the member that governs whether a
    // catalogue arrival is wanted is absent from the generated client, and it defaults to true on
    // the instance. A body without it adds the entity and marks every scene of it wanted, which is
    // the opposite of what this add is for.
    //
    // No after date, because the whole catalogue is the point: this generation refuses to add a
    // scene older than that date at all, so a date here would hide most of what a reader is missing.
    internal static JsonObject TrackEntity(string foreignId, AddDefaults defaults)
    {
        Require(foreignId, defaults);

        return new JsonObject
        {
            ["foreignId"] = foreignId,
            ["rootFolderPath"] = defaults.RootFolderPath,
            ["qualityProfileId"] = defaults.QualityProfileId,
            ["tags"] = new JsonArray(),

            // The instance syncs a catalogue only for an entity carrying this flag, so it is what
            // makes the entity's scenes listable at all.
            ["monitored"] = true,

            // Every scene the sync brings in arrives unmonitored, and nothing is searched.
            ["whisparrMonitorNewItems"] = false,
            ["searchOnAdd"] = NoAcquisition,

            // Movie-type items need a metadata link this product never adds.
            ["moviesMonitored"] = false,
        };
    }

    // The monitor type covers the scene alone; a wider type would monitor items nobody asked
    // about. No profile is read off the parent entity: a scene the instance's own catalogue
    // refresh creates inherits its parent's profile from the instance.
    //
    // The title member carries the identifier because a scene add with no title is refused
    // outright. The instance validates it for emptiness and then replaces it with what its metadata
    // provider answers, so no value sent here survives. Both halves are pinned.
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

    // An id array, which is this generation's spelling. v2 names a single scalar id, and a body
    // carrying the other's shape is accepted and does nothing.
    internal static JsonObject RefreshCatalogue(WhisparrEntityKind kind, int entityId)
        => kind switch
        {
            WhisparrEntityKind.Studio => Command(RefreshStudiosCommand, "studioIds", entityId),
            WhisparrEntityKind.Performer
                => Command(RefreshPerformersCommand, "performerIds", entityId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };

    // Composed only for a caller holding the grabbing role: this is a body that makes an instance
    // acquire.
    internal static JsonObject SearchMonitored(WhisparrEntityKind kind, int entityId)
        => kind switch
        {
            WhisparrEntityKind.Studio => Command(StudiosSearchCommand, "studioIds", entityId),
            WhisparrEntityKind.Performer => Command(PerformersSearchCommand, "performerIds", entityId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };

    // One command naming every entity: the id member is an array and the instance iterates all of
    // it. An id the instance does not hold fails the whole command and the answer names only the
    // first such id, so a caller composes this over entities it knows the instance holds.
    internal static JsonObject SearchAllMonitored(
        WhisparrEntityKind kind, IReadOnlyList<int> entityIds)
        => kind switch
        {
            WhisparrEntityKind.Studio => Command(StudiosSearchCommand, "studioIds", entityIds),
            WhisparrEntityKind.Performer
                => Command(PerformersSearchCommand, "performerIds", entityIds),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };

    // Composed only for a caller holding the per-scene grabbing role: this is a body that makes an
    // instance acquire.
    internal static JsonObject SearchScene(int sceneId)
        => Command(ScenesSearchCommand, SceneIdsProperty, sceneId);

    internal static JsonObject Command(string name, string idsProperty, int entityId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return Command(name, idsProperty, (IReadOnlyList<int>)[entityId]);
    }

    // An empty array is refused rather than composed. The instance accepts a command whose id
    // array is empty and runs it over nothing, which a reader cannot tell from a search that found
    // nothing.
    internal static JsonObject Command(string name, string idsProperty, IReadOnlyList<int> entityIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(idsProperty);
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentOutOfRangeException.ThrowIfZero(entityIds.Count);
        foreach (var entityId in entityIds)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        }

        return new JsonObject
        {
            ["name"] = name,
            [idsProperty] = new JsonArray([.. entityIds.Select(entityId => (JsonNode)entityId)]),
        };
    }

    // Every other field of the editor resource is nullable and an omitted one is not applied, so
    // the profile, root folder, tags and date gate the instance holds are left alone.
    internal static StudioEditorResource SetStudioMonitored(int entityId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new StudioEditorResource(studioIds: new List<int> { entityId }, monitored: monitored);
    }

    // The patch resource declares the flag and no other member, so everything else the instance
    // holds for that scene is left alone. The scene is named by the route's own segment.
    internal static MoviePatchResource SceneMonitorPatch(bool monitored)
        => new(monitored: monitored);

    // The instance validates the display title as non-empty and binds it as movieTitle, so a body
    // carrying the identifier alone, or a plain title, is refused before anything is written. The
    // title is composed from the identifier because the exclusion is offered for a scene the
    // instance holds no row to take a name from, and the instance keeps what is sent. The type is
    // stated rather than left to the instance's default, which is the instance's to change.
    internal static ImportListExclusionResource SceneExclusion(string foreignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        return new ImportListExclusionResource(
            foreignId: foreignId,
            movieTitle: SceneExclusionTitle(foreignId),
            type: SceneExclusionType);
    }

    internal const ImportExclusionType SceneExclusionType = ImportExclusionType.Scene;

    internal static string SceneExclusionTitle(string foreignId) => $"Scene {foreignId}";

    internal static PerformerEditorResource SetPerformerMonitored(int entityId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new PerformerEditorResource(performerIds: new List<int> { entityId }, monitored: monitored);
    }

    // The whole resource, not the editor one: the editor resource declares no date gate, so a scope
    // change sent there is accepted and applies nothing.
    //
    // The only body cloned from a whole instance response, so it carries members nothing here wrote.
    // The suppression flag is overwritten, not removed: the read echoes it, an entity added through
    // the instance's own interface with search-on-add ticked holds it true, and omitting it would
    // rely on a default nobody measured. Add-options is overwritten only where the clone already
    // carries one, this schema declaring none.
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

    // A zero quality profile id is refused here: this generation accepts one, echoes it back, and
    // the entity then monitors and can never acquire anything.
    private static void Require(string foreignId, AddDefaults defaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaults.RootFolderPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaults.QualityProfileId, 1);
    }
}
