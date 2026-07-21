using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisparrSync.Client;

/// <summary>
/// The Whisparr v3 <c>GET /api/v3/system/status</c> projection. Field set transcribed from the
/// devopsarr/whisparr-go generated <c>SystemResource</c> model; only the fields the extension needs are
/// modeled. <see cref="Version"/> is the dotted app version (e.g. <c>3.3.4.808</c>) whose major segment
/// the adapter gate reads; <see cref="InstanceName"/> is shown on a successful Test connection.
/// </summary>
internal sealed record SystemStatus(string? Version, string? AppName, string? InstanceName, string? Branch);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/rootfolder</c> row. Field set transcribed from the devopsarr/whisparr-go
/// generated <c>RootFolderResource</c> model; only the fields the settings UI needs are modeled.
/// <see cref="Accessible"/> is Whisparr's own reachability flag and <see cref="FreeSpace"/> is bytes free
/// (both nullable-friendly so a partial row still deserializes).
/// </summary>
internal sealed record RootFolder(int Id, string? Path, bool Accessible, long? FreeSpace);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/qualityprofile</c> row. Field set transcribed from the
/// devopsarr/whisparr-go generated <c>QualityProfileResource</c> model; the settings UI needs only the
/// stable <see cref="Id"/> (the persisted selection) and the display <see cref="Name"/>.
/// </summary>
internal sealed record QualityProfile(int Id, string? Name);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/movie</c> row — the reconciliation data source. Field set
/// transcribed from the devopsarr/whisparr-go generated <c>MovieResource</c> model and confirmed against
/// the live v3.3.4.794 instance; only the fields identity matching needs are modeled (nullable-friendly
/// so a partial row still deserializes). The full set is returned unpaged (issue #218), so the
/// <see cref="StashId"/> index is built client-side rather than via a server-side query.
/// </summary>
/// <remarks>
/// Field polymorphism (why the matcher must not treat these uniformly): <see cref="StashId"/> is the
/// scene's StashDB UUID when present; <see cref="ForeignId"/> is a tmdbId when <see cref="ItemType"/>
/// is <c>"movie"</c> and the StashDB UUID when <see cref="ItemType"/> is <c>"scene"</c> — so a Cove
/// StashDB id must be matched against <see cref="StashId"/> first, and against <see cref="ForeignId"/>
/// only for scene rows.
/// <para>
/// Attribution (count scoping): <see cref="StudioTitle"/> and
/// <see cref="PerformerForeignIds"/> are the ONLY movie-side handles that attribute a movie to a
/// monitored studio/performer. VERIFIED against the live v3.3.x instance: the movie row carries a
/// studio <em>title</em> (<c>studioTitle</c>) but NO studio foreign id, so a studio must be attributed
/// by title, whereas a performer is attributed by StashDB id via <c>performerForeignIds</c>. Both are
/// optional/nullable so an unexpected shape degrades to null, never a throw; both are append-only so the
/// existing reconciliation params are unchanged in name and order.
/// </para>
/// <para>
/// Flip round-trip: <see cref="RootFolderPath"/> and <see cref="Tags"/> are carried so a
/// monitor toggle's <c>PUT /movie/{id}</c> can echo the existing resource (root + origin tag) rather than
/// clearing them. Both nullable and append-only (after the existing optionals) so positional construction
/// elsewhere (V2Adapter, tests) is unchanged.
/// </para>
/// <para>
/// Flip <c>path</c>: Whisparr Eros's <c>PUT /movie/{id}</c> REJECTS a body with no
/// top-level <see cref="Path"/> ("'Path' must not be empty."). This is the movie's own on-disk directory
/// (distinct from <c>MovieFile.Path</c>, the downloaded file), returned on every GET/create; a monitor flip
/// must echo it back. Nullable and append-only (last) so positional construction elsewhere is unchanged.
/// </para>
/// <para>
/// v2 owned-import back-reference: <see cref="SeriesId"/> is null on a genuine v3 movie row and set
/// ONLY on a v2-synthesized scene (a v2 scene = a Sonarr episode, which is imported through its enclosing
/// series). The owned-import path needs the series id to target the <c>ManualImport</c> at the right episode.
/// Nullable and append-only (last) so v3 rows and existing positional construction are unaffected.
/// </para>
/// </remarks>
internal sealed record WhisparrMovie(
    int Id,
    string? Title,
    int? Year,
    string? StashId,
    string? ForeignId,
    string? ItemType,
    bool Monitored,
    bool HasFile,
    WhisparrMovieFile? MovieFile,
    string? StudioTitle = null,
    string[]? PerformerForeignIds = null,
    int? QualityProfileId = null,
    bool? QualityCutoffNotMet = null,
    string? RootFolderPath = null,
    int[]? Tags = null,
    string? Path = null,
    int? SeriesId = null);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/studio</c> row — also the <c>?stashId={id}</c> lookup element and the
/// projection returned by a studio POST/PUT. Field set VERIFIED against the live v3.3.x instance; only
/// the monitor-relevant fields are modeled (nullable-friendly so a partial row still binds).
/// <see cref="ForeignId"/> is the StashDB studio id the <c>?stashId=</c> query matches; <see cref="Monitored"/>
/// is the flag the studio add-then-flip toggles; <see cref="QualityProfileId"/> + <see cref="RootFolderPath"/>
/// carry the defaults back on a read. <see cref="SceneCount"/>/<see cref="TotalSceneCount"/> are Whisparr's
/// own present-in-library / full-StashDB-catalog counts, surfaced verbatim as the entity status count.
/// </summary>
internal sealed record WhisparrStudio(
    int Id,
    string? ForeignId,
    string? Title,
    bool Monitored,
    int? QualityProfileId,
    string? RootFolderPath,
    int[]? Tags,
    int SceneCount = 0,
    int TotalSceneCount = 0);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/performer/{stashId}</c> row — also the projection returned by a performer
/// POST/PUT. Field set VERIFIED against the live v3.3.x instance; only the monitor-relevant fields are
/// modeled (nullable-friendly). <see cref="ForeignId"/> is the StashDB performer id; <see cref="Monitored"/>
/// is the flag the performer add-then-flip toggles. A not-added performer answers HTTP 404/500 (classified
/// <see cref="WhisparrResultState.Absent"/>) rather than returning this shape. <see cref="SceneCount"/>/
/// <see cref="TotalSceneCount"/> are Whisparr's own present / full-catalog counts, surfaced as the status count.
/// </summary>
internal sealed record WhisparrPerformer(
    int Id,
    string? ForeignId,
    string? FullName,
    bool Monitored,
    int? QualityProfileId,
    string? RootFolderPath,
    int[]? Tags,
    int SceneCount = 0,
    int TotalSceneCount = 0);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/tag</c> row (also the projection returned by a tag POST) — the
/// origin-tag lookup/create surface. Only <see cref="Id"/> (the value applied to a studio/
/// performer add) and <see cref="Label"/> (the lookup key) are modeled; nullable-friendly so a partial
/// row still binds.
/// </summary>
internal sealed record WhisparrTag(int Id, string? Label);

/// <summary>
/// The on-disk file of a <see cref="WhisparrMovie"/> (the <c>movieFile</c> sub-resource). <see cref="Path"/>
/// is the source of the reconciliation path leg; nullable because a not-yet-downloaded movie has none.
/// <see cref="Quality"/> is the append-only quality leg (<c>movieFile.quality.quality.name</c>),
/// nullable so an older/partial row still binds and the existing positional construction (V2Adapter) is
/// unchanged.
/// </summary>
internal sealed record WhisparrMovieFile(int Id, string? Path, WhisparrFileQuality? Quality = null);

/// <summary>
/// The <c>movieFile.quality</c> wrapper — Whisparr nests the quality name one level deeper under a
/// <c>quality</c> object (<c>movieFile.quality.quality.name</c>). Modeled only to reach that name for the
/// scene panel; nullable-friendly so a partial row degrades to null rather than throwing.
/// </summary>
internal sealed record WhisparrFileQuality(WhisparrQualityName? Quality);

/// <summary>
/// The inner <c>quality</c> object carrying the human display <see cref="Name"/> (e.g. <c>WEB-DL 1080p</c>)
/// the scene panel renders. Nullable so an absent quality name degrades gracefully.
/// </summary>
internal sealed record WhisparrQualityName(string? Name);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/exclusions</c> row — an import-list exclusion. <see cref="ForeignId"/>
/// is the scene's StashDB id the <c>SceneStatusProjector</c> matches to resolve the <c>Excluded</c> state;
/// <see cref="Title"/>/<see cref="Year"/> are carried for display only and bind Eros's <c>movieTitle</c>/
/// <c>movieYear</c> fields. Every field nullable so a partial row still binds and a hostile/odd body
/// degrades to null rather than throwing.
/// </summary>
internal sealed record WhisparrExclusion(
    int Id,
    string? ForeignId,
    [property: JsonPropertyName("movieTitle")] string? Title,
    [property: JsonPropertyName("movieYear")] int? Year);

/// <summary>
/// One Whisparr v3 <c>GET /api/v3/release?movieId={id}</c> indexer result row. The count still needs
/// only <see cref="Guid"/> + <see cref="Title"/>, but the interactive picker renders and grabs a row,
/// so the display + grab fields are modeled too (all append-only and nullable, so a partial row still binds
/// and the existing positional construction elsewhere is unchanged).
/// </summary>
/// <remarks>
/// The grab handles are <see cref="Guid"/> + <see cref="IndexerId"/> — the pair Whisparr's
/// <c>POST /api/v3/release</c> requires to grab THIS release (a guid alone is ambiguous across indexers).
/// <see cref="Quality"/> reuses the <see cref="WhisparrFileQuality"/> wrapper because a release nests its
/// quality name identically to a movie file (<c>quality.quality.name</c>); the picker reads
/// <c>Quality?.Quality?.Name</c> for the display label.
/// </remarks>
internal sealed record WhisparrRelease(
    string? Guid,
    string? Title,
    WhisparrFileQuality? Quality = null,
    long? Size = null,
    string? Indexer = null,
    int? IndexerId = null,
    int? Seeders = null,
    int? Age = null);

/// <summary>
/// One page of the Whisparr v3 <c>GET /api/v3/history</c> envelope (VERIFIED live shape:
/// <c>{ page, pageSize, totalRecords, records[] }</c>). The reconcile backstop pages this
/// newest-first until it reaches the stored checkpoint. <see cref="Records"/> is nullable so an empty or
/// partial envelope still deserializes to a no-op.
/// </summary>
internal sealed record WhisparrHistoryPage(int Page, int PageSize, int TotalRecords, WhisparrHistoryRecord[]? Records);

/// <summary>
/// A single Whisparr history row. Parsed defensively (every field nullable, unknown props ignored) against
/// the Radarr-family contract — the reconcile filters on <see cref="EventType"/> (the import type is
/// <c>downloadFolderImported</c>) and reads the imported path + download id out of the free-form
/// <see cref="Data"/> map (<c>importedPath</c> / <c>droppedPath</c> / <c>downloadId</c>). <see cref="Id"/>
/// is the stable, monotonically-increasing record id the checkpoint high-water mark tracks.
/// </summary>
internal sealed record WhisparrHistoryRecord(
    int Id,
    int MovieId,
    string? Date,
    string? EventType,
    Dictionary<string, string>? Data);

/// <summary>
/// A Whisparr v2 <c>GET /api/v3/series</c> row — a studio/site (Whisparr v2 is Sonarr-based, so content is
/// modeled as series → episodes). v2 has no <c>/movie</c> entity, so <c>V2Adapter</c> walks
/// series → episode → episodefile to synthesize the normalized <c>WhisparrMovie[]</c>. Field set VERIFIED
/// against the live v2 2.2.0.108 instance; nullable-friendly so a
/// partial row still deserializes. <see cref="TvdbId"/> is the TPDB *site* id — v2 carries no StashDB id.
/// </summary>
/// <remarks>
/// This row doubles as the <c>series/lookup</c> element and the <c>POST</c>/<c>PUT /series</c> projection
/// (the outward add/monitor path). A lookup row has no <c>id</c> (not added yet → binds 0). The
/// monitor-relevant fields are append-only after the read-side set so existing deserialization/construction
/// is unchanged: <see cref="Monitored"/> is the flag the add-then-flip toggles; <see cref="MonitorNewItems"/>
/// is v2's <c>"all"|"none"</c> new-item policy; <see cref="QualityProfileId"/> + <see cref="RootFolderPath"/>
/// carry the add defaults back on a read.
/// </remarks>
internal sealed record WhisparrSeries(
    int Id,
    int? TvdbId,
    string? Title,
    string? TitleSlug,
    string? Path,
    bool Monitored = false,
    string? MonitorNewItems = null,
    int? QualityProfileId = null,
    string? RootFolderPath = null,
    int[]? Tags = null,
    WhisparrSeriesStatistics? Statistics = null);

/// <summary>
/// The <c>statistics</c> block on a Whisparr v2 <c>GET /api/v3/series</c> row: episode counts carried on the
/// LIST row itself, so a studio's present/catalog count reads off one series-list call with no per-series
/// <c>/episode</c> fetch. <see cref="EpisodeFileCount"/> is episodes with a file (present),
/// <see cref="TotalEpisodeCount"/> the site's full episode catalog.
/// </summary>
internal sealed record WhisparrSeriesStatistics(
    int EpisodeFileCount = 0,
    int EpisodeCount = 0,
    int TotalEpisodeCount = 0);

/// <summary>
/// A Whisparr v2 <c>GET /api/v3/episode?seriesId=N</c> row — one scene under a series. Key set VERIFIED
/// against the live v2 instance: the only scene identity is
/// <see cref="TvdbId"/> (a TPDB *scene* id repurposed into Sonarr's <c>tvdbId</c> field) — there is no
/// <c>stashId</c>/<c>foreignId</c>/<c>imdbId</c> anywhere (0/627 scenes). <see cref="EpisodeFileId"/> joins
/// to the <see cref="WhisparrEpisodeFile"/> that carries the on-disk path (0 when not downloaded).
/// Nullable-friendly so a partial row still deserializes.
/// </summary>
internal sealed record WhisparrEpisode(
    int Id,
    string? Title,
    string? ReleaseDate,
    int EpisodeFileId,
    int? TvdbId,
    int SeriesId,
    bool HasFile,
    bool Monitored);

/// <summary>
/// A Whisparr v2 <c>GET /api/v3/episodefile?seriesId=N</c> row — the on-disk file of an episode (scene),
/// the source of the reconciliation path leg. <see cref="Path"/> is nullable because a partial/absent row
/// still deserializes; the adapter joins <see cref="Id"/> back to the episode's <c>episodeFileId</c>.
/// </summary>
internal sealed record WhisparrEpisodeFile(int Id, int SeriesId, string? Path);

/// <summary>
/// One Whisparr <c>GET /api/v3/manualimport?folder={dir}&amp;filterExistingFiles=false</c> candidate row —
/// the input to a targeted in-place <c>ManualImport</c> (the v2 owned-scene import). The listing is matched to
/// the owned file by <see cref="Path"/> (case- and separator-normalized).
/// </summary>
/// <remarks>
/// <see cref="Quality"/> and <see cref="Languages"/> are held as raw <see cref="JsonElement"/> so they
/// round-trip VERBATIM back into the <c>ManualImport</c> command body: a synthesized quality object does not
/// import (verified live), so the exact object Whisparr listed must be echoed unchanged. <see cref="Rejections"/>
/// is carried but deliberately IGNORED by the owned-import path — a name-parse rejection like "Invalid season or
/// episode" is expected on a targeted import and is overridden by the explicit <c>episodeIds</c>.
/// </remarks>
internal sealed record WhisparrManualImportItem(
    string? Path,
    JsonElement Quality,
    JsonElement Languages,
    JsonElement Rejections);

/// <summary>
/// The Whisparr v3 <c>GET /api/v3/config/naming</c> singleton. Carries the WHOLE config object verbatim so a
/// write is a safe read-modify-write.
/// </summary>
/// <remarks>
/// A config resource is a whole-object singleton: a <c>PUT</c> replaces the entire resource, so a partial
/// body wipes every field it omits. Every field Whisparr did not model here is captured in
/// <see cref="Extra"/> as a raw <see cref="JsonElement"/> (the same verbatim round-trip
/// <see cref="WhisparrManualImportItem"/> uses for <c>Quality</c>/<c>Languages</c>), so re-serializing after
/// flipping one boolean re-emits every field the <c>GET</c> returned — the unknown fields survive byte-for-
/// value. Only the two file-affecting booleans are typed; the field names are the assumed Radarr/Whisparr
/// Eros contract (confirmed live at the phase gate). Both bind case-insensitively and default off, so a
/// partial/odd body still binds.
/// </remarks>
internal sealed record NamingConfig
{
    [JsonPropertyName("renameMovies")]
    public bool RenameMovies { get; init; }

    [JsonPropertyName("replaceIllegalCharacters")]
    public bool ReplaceIllegalCharacters { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

/// <summary>
/// The Whisparr v3 <c>GET /api/v3/config/mediamanagement</c> singleton. Same whole-object round-trip contract
/// as <see cref="NamingConfig"/> (see its remarks): unknown fields round-trip verbatim through
/// <see cref="Extra"/> so a read-modify-write flip of one boolean never drops the rest of the singleton.
/// </summary>
internal sealed record MediaManagementConfig
{
    [JsonPropertyName("autoRenameFolders")]
    public bool AutoRenameFolders { get; init; }

    [JsonPropertyName("deleteEmptyFolders")]
    public bool DeleteEmptyFolders { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

/// <summary>
/// The config-write request the UI sends: the four file-affecting toggles it may flip. Every field is
/// nullable so a request changes ONLY the toggles it carries — an absent field leaves that setting at its
/// current Whisparr value (the server read-modify-writes it). The server honors ONLY these four booleans; it
/// never accepts or forwards an arbitrary Whisparr config object.
/// </summary>
internal sealed record WhisparrFileSettingsRequest(
    bool? RenameMovies = null,
    bool? ReplaceIllegalCharacters = null,
    bool? AutoRenameFolders = null,
    bool? DeleteEmptyFolders = null);

/// <summary>
/// The four file-affecting Whisparr toggles, read off the naming (<see cref="RenameMovies"/>,
/// <see cref="ReplaceIllegalCharacters"/>) and media-management (<see cref="AutoRenameFolders"/>,
/// <see cref="DeleteEmptyFolders"/>) config singletons — the projection the read + write endpoints return.
/// </summary>
internal sealed record WhisparrFileSettings(
    bool RenameMovies,
    bool ReplaceIllegalCharacters,
    bool AutoRenameFolders,
    bool DeleteEmptyFolders);

/// <summary>
/// The queued-command handle returned by <c>POST /api/v3/command</c> and re-read by
/// <c>GET /api/v3/command/{id}</c> — the two fields a caller needs to WAIT for an asynchronous command
/// (e.g. a targeted <c>RefreshStudios</c>) to finish before acting on its result.
/// </summary>
/// <remarks>
/// A metadata refresh is queued, not synchronous, so acceptance alone (the bool
/// <see cref="WhisparrClient.SendCommandAsync"/> returns) does not mean the catalogue has been populated.
/// <see cref="Id"/> addresses the queued command for a status poll and <see cref="Status"/> carries
/// Whisparr's command-lifecycle string (<c>queued</c>/<c>started</c>/<c>completed</c>/<c>failed</c>),
/// modeled as a free string so the adapter — not the transport — interprets it. <see cref="Status"/> is
/// nullable so a body omitting it still binds rather than throwing.
/// </remarks>
internal sealed record WhisparrCommand(int Id, string? Status);
