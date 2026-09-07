using System.Text.Json;
using System.Text.Json.Serialization;

// Every record here models one Whisparr response row, and they share two conventions that are
// therefore stated once rather than on each type: only the fields this extension consumes are
// modeled, and every field is nullable so a partial or unexpected body binds to null instead of
// throwing. What earns a per-type comment is a Whisparr behaviour the shape alone cannot show.
namespace WhisparrSync.Client;

/// <summary>
/// The Whisparr v3 <c>GET /api/v3/system/status</c> projection. <see cref="Version"/> is the dotted app
/// version (e.g. <c>3.3.4.808</c>) whose major segment the adapter gate reads; <see cref="InstanceName"/>
/// is shown on a successful Test connection.
/// </summary>
internal sealed record SystemStatus(string? Version, string? AppName, string? InstanceName, string? Branch);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/rootfolder</c> row. <see cref="Accessible"/> is Whisparr's own
/// reachability flag; <see cref="FreeSpace"/> is bytes free.
/// </summary>
internal sealed record RootFolder(int Id, string? Path, bool Accessible, long? FreeSpace);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/qualityprofile</c> row. The settings UI needs only the stable
/// <see cref="Id"/> (the persisted selection) and the display <see cref="Name"/>.
/// </summary>
internal sealed record QualityProfile(int Id, string? Name);

/// <summary>
/// The members the scene-status classification reads off a Whisparr movie, and nothing else.
/// </summary>
/// <remarks>
/// A whole-library read can afford to bind a narrow projection of each row instead of the full one, but the
/// classifier must key BOTH shapes by the one rule — two copies would let a scene's status pill and the action
/// taken on it disagree about which movie the scene is. Declaring the members it reads as a contract is what
/// makes a projection that omits one a compile error rather than a silent null on the wire, which would report
/// a wrong status for every scene in the library without failing anything.
/// </remarks>
internal interface IWhisparrMovieFacts
{
    string? StashId { get; }

    string? ForeignId { get; }

    string? ItemType { get; }

    bool Monitored { get; }

    bool HasFile { get; }
}

/// <summary>
/// The narrow binding of a <c>GET /api/v3/movie</c> row: the five members
/// <see cref="IWhisparrMovieFacts"/> declares and no others.
/// </summary>
/// <remarks>
/// An unmapped JSON member costs nothing to skip, so binding this instead of the full row over a whole-library
/// response retains a small fraction of the bytes for the same answer. It deliberately carries neither the
/// nested quality name nor the cutoff flag — a caller needing those is asking a per-scene question and reads
/// the full row.
/// </remarks>
internal sealed record WhisparrMovieFacts(
    string? StashId,
    string? ForeignId,
    string? ItemType,
    bool Monitored,
    bool HasFile) : IWhisparrMovieFacts;

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/movie</c> row — the reconciliation data source. The full set is
/// returned unpaged (issue #218), so the <see cref="StashId"/> index is built client-side rather than
/// via a server-side query.
/// </summary>
/// <remarks>
/// The matcher must not treat the two id fields uniformly: <see cref="StashId"/> is the scene's StashDB
/// UUID when present, while <see cref="ForeignId"/> is a tmdbId when <see cref="ItemType"/> is
/// <c>"movie"</c> and the StashDB UUID when it is <c>"scene"</c>. A Cove StashDB id is therefore matched
/// against <see cref="StashId"/> first, and against <see cref="ForeignId"/> only for scene rows.
/// <para>
/// Attribution is symmetric: <see cref="StudioForeignId"/> and <see cref="PerformerForeignIds"/> are
/// both StashDB ids, so a studio and a performer are each attributed by identity rather than by name.
/// <see cref="StudioTitle"/> is display text only. The two travel together — this build emits no row
/// carrying a studio title without also carrying the studio's foreign id.
/// </para>
/// <para>
/// Eros's <c>PUT /movie/{id}</c> rejects a body with no top-level <see cref="Path"/> ("'Path' must not
/// be empty."), so a monitor flip echoes it back along with <see cref="RootFolderPath"/> and
/// <see cref="Tags"/> — omitting those clears the root and origin tag. <see cref="Path"/> is the movie's
/// own on-disk directory, distinct from <c>MovieFile.Path</c>, the downloaded file.
/// </para>
/// <para>
/// <see cref="SeriesId"/> is null on a genuine v3 movie row and set only on a v2-synthesized scene: a v2
/// scene is a Sonarr episode, imported through its enclosing series, and the owned-import path needs the
/// series id to target the <c>ManualImport</c> at the right episode.
/// </para>
/// <para>
/// <see cref="Images"/> holds the poster under the entry whose <c>coverType</c> is the poster kind.
/// <see cref="Added"/> is when the scene entered Whisparr's set (the activity Wanted row's "added" date);
/// a v2 episode-shaped row carries no such field.
/// </para>
/// <para>
/// <see cref="PerformerNames"/> and <see cref="PerformerForeignIds"/> are index-aligned, so a
/// through-Whisparr card can name its performers. What the movie row does NOT carry is a performer
/// <em>image</em> or a tag name: <see cref="PerformerImageUrls"/> and <see cref="TagNames"/> are
/// populated only by the direct provider (StashDB), which is why a through-Whisparr chip's avatar is
/// resolved downstream from Cove's own performer records instead.
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
    string? StudioForeignId = null,
    string[]? PerformerForeignIds = null,
    int? QualityProfileId = null,
    bool? QualityCutoffNotMet = null,
    string? RootFolderPath = null,
    int[]? Tags = null,
    string? Path = null,
    int? SeriesId = null,
    string? ReleaseDate = null,
    WhisparrImage[]? Images = null,
    string? Added = null,
    string[]? PerformerNames = null,
    string[]? TagNames = null,
    string? Overview = null,
    string[]? PerformerImageUrls = null) : IWhisparrMovieFacts
{
    /// <summary>This row reduced to the five members the status classification reads.</summary>
    /// <remarks>
    /// The converging point for a caller that already holds full rows — v2 synthesizes them from
    /// series → episode and has no narrower thing to read — so both generations reach the classifier through
    /// one index type rather than two.
    /// </remarks>
    public WhisparrMovieFacts Facts => new(StashId, ForeignId, ItemType, Monitored, HasFile);
}

/// <summary>
/// One image on a Whisparr movie row (an <c>images</c> array entry). <see cref="CoverType"/> is the kind
/// (<c>"poster"</c>/<c>"screenshot"</c>/…); the discovery projection reads the poster entry's url.
/// <see cref="RemoteUrl"/> is the source-served absolute url (preferred for direct render);
/// <see cref="Url"/> is the host-relative cached path.
/// </summary>
internal sealed record WhisparrImage(string? CoverType, string? Url, string? RemoteUrl);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/studio</c> row — also the <c>?stashId={id}</c> lookup element and the
/// projection returned by a studio POST/PUT. <see cref="ForeignId"/> is the StashDB studio id the
/// <c>?stashId=</c> query matches; <see cref="Monitored"/> is the flag the studio add-then-flip toggles;
/// <see cref="QualityProfileId"/> + <see cref="RootFolderPath"/> carry the defaults back on a read.
/// <see cref="SceneCount"/>/<see cref="TotalSceneCount"/> are Whisparr's own present-in-library /
/// full-StashDB-catalog counts, surfaced verbatim as the entity status count.
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
/// POST/PUT. <see cref="ForeignId"/> is the StashDB performer id; <see cref="Monitored"/> is the flag the
/// performer add-then-flip toggles. A not-added performer answers HTTP 404/500 (classified
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
/// origin-tag lookup/create surface. <see cref="Id"/> is the value applied to a studio/performer add;
/// <see cref="Label"/> is the lookup key.
/// </summary>
internal sealed record WhisparrTag(int Id, string? Label);

/// <summary>
/// A Whisparr <c>GET /api/v3/notification</c> row — a configured notification/connection (also the POST/PUT
/// projection). <see cref="Name"/> is the connection name the register matches ("Cove Whisparr Sync") and
/// <see cref="Fields"/> carries the webhook URL.
/// </summary>
internal sealed record WhisparrNotification(int Id, string? Name, WhisparrNotificationField[]? Fields);

/// <summary>
/// One <c>field</c> of a <see cref="WhisparrNotification"/> — a name/value pair. <see cref="Value"/> stays a
/// raw <see cref="JsonElement"/> because Whisparr's field values are polymorphic (string / number / bool /
/// array); the adapter reads the string when <see cref="Name"/> is <c>"url"</c>.
/// </summary>
internal sealed record WhisparrNotificationField(string? Name, JsonElement Value);

/// <summary>
/// The on-disk file of a <see cref="WhisparrMovie"/> (the <c>movieFile</c> sub-resource). <see cref="Path"/>
/// is the source of the reconciliation path leg — null when the movie is not yet downloaded, which is how
/// the path leg abstains. <see cref="Quality"/> reaches the quality name at
/// <c>movieFile.quality.quality.name</c>.
/// </summary>
internal sealed record WhisparrMovieFile(int Id, string? Path, WhisparrFileQuality? Quality = null);

/// <summary>
/// The <c>movieFile.quality</c> wrapper — Whisparr nests the quality name one level deeper under a
/// <c>quality</c> object (<c>movieFile.quality.quality.name</c>), so reaching it needs two hops.
/// </summary>
internal sealed record WhisparrFileQuality(WhisparrQualityName? Quality);

/// <summary>
/// The inner <c>quality</c> object carrying the human display <see cref="Name"/> (e.g. <c>WEB-DL 1080p</c>)
/// the scene panel renders.
/// </summary>
internal sealed record WhisparrQualityName(string? Name);

/// <summary>
/// A Whisparr v3 <c>GET /api/v3/exclusions</c> row — an import-list exclusion. <see cref="ForeignId"/>
/// is the scene's StashDB id the <c>SceneStatusProjector</c> matches to resolve the <c>Excluded</c> state;
/// <see cref="Title"/>/<see cref="Year"/> are display-only and bind Eros's <c>movieTitle</c>/
/// <c>movieYear</c> fields.
/// </summary>
internal sealed record WhisparrExclusion(
    int Id,
    string? ForeignId,
    [property: JsonPropertyName("movieTitle")] string? Title,
    [property: JsonPropertyName("movieYear")] int? Year);

/// <summary>
/// One Whisparr v3 <c>GET /api/v3/release?movieId={id}</c> indexer result row — a release count needs
/// only <see cref="Guid"/> + <see cref="Title"/>, while the interactive picker also renders and grabs.
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
/// newest-first until it reaches the stored checkpoint.
/// </summary>
internal sealed record WhisparrHistoryPage(int Page, int PageSize, int TotalRecords, WhisparrHistoryRecord[]? Records);

/// <summary>
/// A single Whisparr history row, read against the Radarr-family contract — the reconcile filters on
/// <see cref="EventType"/> (the import type is <c>downloadFolderImported</c>) and reads the imported path +
/// download id out of the free-form <see cref="Data"/> map (<c>importedPath</c> / <c>droppedPath</c> /
/// <c>downloadId</c>). <see cref="Id"/> is the stable, monotonically-increasing record id the checkpoint
/// high-water mark tracks; <see cref="SourceTitle"/> is the activity History projection's display title.
/// </summary>
internal sealed record WhisparrHistoryRecord(
    int Id,
    int MovieId,
    string? Date,
    string? EventType,
    Dictionary<string, string>? Data,
    string? SourceTitle = null);

/// <summary>
/// One page of the Whisparr <c>GET /api/v3/queue</c> envelope (<c>{ page, pageSize, totalRecords, records[] }</c>,
/// the same paging shape as <see cref="WhisparrHistoryPage"/>). Both generations serve this path (v2 is
/// Sonarr-shaped); the projection normalizes either row into the uniform queue response.
/// </summary>
internal sealed record WhisparrQueuePage(int Page, int PageSize, int TotalRecords, WhisparrQueueRecord[]? Records);

/// <summary>
/// A single Whisparr queue row, read against the Radarr/Sonarr <c>QueueResource</c> contract. The
/// projection reads <see cref="Status"/> / <see cref="TrackedDownloadState"/> /
/// <see cref="TrackedDownloadStatus"/> for the normalized queue state, <see cref="Size"/> / <see cref="Sizeleft"/>
/// for the progress figure, <see cref="Timeleft"/> for the ETA, and the display title/studio/quality.
/// </summary>
/// <remarks>
/// <see cref="Size"/>/<see cref="Sizeleft"/> are modeled as <see cref="double"/> because the Servarr schema types
/// them <c>number($double)</c> (a byte count that may deserialize with a fractional part). <see cref="Quality"/>
/// reuses <see cref="WhisparrFileQuality"/> because the queue row's <c>quality</c> nests its name identically to a
/// movie file (<c>quality.quality.name</c>). <see cref="Movie"/> is the v3 scene (its <c>studioTitle</c> is the
/// queue-row studio); <see cref="Series"/> is the v2 (Sonarr) site the queue row carries instead — the projection
/// reads whichever is present so one shape covers both generations.
/// </remarks>
internal sealed record WhisparrQueueRecord(
    int Id,
    string? Title,
    string? Status,
    string? TrackedDownloadState,
    string? TrackedDownloadStatus,
    double? Size,
    double? Sizeleft,
    string? Timeleft,
    string? EstimatedCompletionTime,
    string? ErrorMessage,
    WhisparrFileQuality? Quality = null,
    WhisparrMovie? Movie = null,
    WhisparrSeries? Series = null);

/// <summary>
/// One page of the Whisparr <c>GET /api/v3/wanted/missing</c> envelope (the same paged shape as
/// <see cref="WhisparrHistoryPage"/>). On v3 the records are <see cref="WhisparrMovie"/> rows; on v2 they are
/// Sonarr episode-shaped rows whose shared field names (<c>id</c>/<c>title</c>/<c>monitored</c>/<c>hasFile</c>/
/// <c>releaseDate</c>/<c>seriesId</c>) bind into the same <see cref="WhisparrMovie"/> record — so one DTO
/// carries both generations for the projection.
/// </summary>
internal sealed record WhisparrMoviePage(int Page, int PageSize, int TotalRecords, WhisparrMovie[]? Records);

/// <summary>
/// A Whisparr v2 <c>GET /api/v3/series</c> row — a studio/site (Whisparr v2 is Sonarr-based, so content is
/// modeled as series → episodes). v2 has no <c>/movie</c> entity, so <c>V2Adapter</c> walks
/// series → episode → episodefile to synthesize the normalized <c>WhisparrMovie[]</c>.
/// <see cref="TvdbId"/> is the TPDB *site* id — v2 carries no StashDB id.
/// </summary>
/// <remarks>
/// This row doubles as the <c>series/lookup</c> element and the <c>POST</c>/<c>PUT /series</c> projection
/// (the outward add/monitor path). A lookup row has no <c>id</c> — not added yet, so it binds 0.
/// <see cref="Monitored"/> is the flag the add-then-flip toggles; <see cref="MonitorNewItems"/> is v2's
/// <c>"all"|"none"</c> new-item policy; <see cref="QualityProfileId"/> + <see cref="RootFolderPath"/> carry
/// the add defaults back on a read.
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
/// A Whisparr v2 <c>GET /api/v3/episode?seriesId=N</c> row — one scene under a series. The only scene
/// identity is <see cref="TvdbId"/>, a TPDB *scene* id repurposed into Sonarr's <c>tvdbId</c> field; a live
/// v2 instance carries no <c>stashId</c>/<c>foreignId</c>/<c>imdbId</c> on any of its 627 scenes.
/// <see cref="EpisodeFileId"/> joins to the <see cref="WhisparrEpisodeFile"/> that carries the on-disk path,
/// and is 0 when not downloaded.
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
/// the source of the reconciliation path leg. The adapter joins <see cref="Id"/> back to the episode's
/// <c>episodeFileId</c>.
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
/// body wipes every field it omits. <see cref="Extra"/> therefore captures every field not typed here as a
/// raw <see cref="JsonElement"/>, and re-serializing after flipping one boolean re-emits everything the
/// <c>GET</c> returned. Only three fields are typed: the two file-affecting booleans and
/// <see cref="SceneFolderFormat"/>, whose leading literal the folder-overlap advisory reads.
/// </remarks>
internal sealed record NamingConfig
{
    [JsonPropertyName("renameMovies")]
    public bool RenameMovies { get; init; }

    [JsonPropertyName("replaceIllegalCharacters")]
    public bool ReplaceIllegalCharacters { get; init; }

    /// <summary>
    /// Eros's Scene Folder Format template (e.g. <c>scenes/{Studio CleanNetwork}/{Studio CleanTitle}</c>) — the
    /// per-movie folder Eros appends to the root. Its leading literal segment is what the folder-overlap
    /// advisory compares against each root's trailing segment. Nullable: a v2 body or a partial config omits it.
    /// </summary>
    /// <remarks>
    /// Omitted-when-null on write so the read-modify-write in <c>EditFileSettingsAsync</c> never ADDS a
    /// <c>sceneFolderFormat</c> key a GET didn't return — a naming config PUT is a whole-object replace, so
    /// emitting <c>null</c> for a body that lacked the field would wipe it. A GET that carries it round-trips
    /// verbatim.
    /// </remarks>
    [JsonPropertyName("sceneFolderFormat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SceneFolderFormat { get; init; }

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
/// modeled as a free string so the adapter — not the transport — interprets it.
/// </remarks>
internal sealed record WhisparrCommand(int Id, string? Status);

/// <summary>
/// The generated OpenAPI document the instance serves at <c>/docs/v3/openapi.json</c>, reduced to its
/// <see cref="Paths"/> map — the route keys a capability decision reads.
/// </summary>
/// <remarks>
/// Binding a whole document is acceptable here and nowhere else in this client: its size is set by the
/// Whisparr build's API surface, which is fixed by the binary, not by how many scenes the Cove library holds.
/// Every other member is skipped by the source-generated reader at no cost, so only the path keys are
/// materialized. The values are kept as raw <see cref="JsonElement"/> because nothing reads inside an
/// operation object — presence of the key is the whole answer.
/// </remarks>
internal sealed record OpenApiDocument(Dictionary<string, JsonElement>? Paths);
