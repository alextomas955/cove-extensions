namespace WhisparrSync.Client;

/// <summary>
/// The Whisparr v3 <c>GET /api/v3/system/status</c> projection. Field set transcribed from the
/// devopsarr/whisparr-go generated <c>SystemResource</c> model; only the fields this phase needs are
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
/// A Whisparr v3 <c>GET /api/v3/movie</c> row — the MATCH-01 reconciliation data source. Field set
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
    WhisparrMovieFile? MovieFile);

/// <summary>
/// The on-disk file of a <see cref="WhisparrMovie"/> (the <c>movieFile</c> sub-resource). <see cref="Path"/>
/// is the source of the reconciliation path leg; nullable because a not-yet-downloaded movie has none.
/// </summary>
internal sealed record WhisparrMovieFile(int Id, string? Path);

/// <summary>
/// One page of the Whisparr v3 <c>GET /api/v3/history</c> envelope (VERIFIED live shape:
/// <c>{ page, pageSize, totalRecords, records[] }</c>). The reconcile backstop (IMPT-02) pages this
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
/// against the live v2 2.2.0.108 instance (04-RESEARCH.md §Endpoint shape parity); nullable-friendly so a
/// partial row still deserializes. <see cref="TvdbId"/> is the TPDB *site* id — v2 carries no StashDB id.
/// </summary>
internal sealed record WhisparrSeries(int Id, int? TvdbId, string? Title, string? TitleSlug, string? Path);

/// <summary>
/// A Whisparr v2 <c>GET /api/v3/episode?seriesId=N</c> row — one scene under a series. Key set VERIFIED
/// against the live v2 instance (04-RESEARCH.md §"The scene identity crux"): the only scene identity is
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
