using WhisparrSync.Client;
using WhisparrSync.Monitor;
using WhisparrSync.Push;

namespace WhisparrSync.Adapters;

/// <summary>How an owned file is attached to its Whisparr scene — the orchestration selects this from the layout.</summary>
internal enum OwnedImportMode
{
    /// <summary>
    /// Re-point the existing movie row's path to Cove's own scene folder and rescan, so Whisparr links the file
    /// already sitting there — zero duplication, regardless of Whisparr's hardlink setting. The folder-per-scene
    /// path (v3).
    /// </summary>
    InPlaceAdopt,

    /// <summary>
    /// Copy the owned file into the movie's own folder via a targeted <c>ManualImport</c> — the fallback the
    /// orchestration selects for a flat (shared-directory) layout, where a path re-point would collide two movies
    /// on one directory.
    /// </summary>
    Copy,
}

/// <summary>
/// The version-adapter port: the anti-corruption boundary between the settings handlers and a specific
/// Whisparr API generation. Every Whisparr call routes through this seam, so the handlers never
/// hold generation-specific wire knowledge — the concrete adapter (e.g. <see cref="V3Adapter"/>) owns the
/// endpoint paths + DTO mapping for its major version. The <see cref="AdapterSelector"/> chooses the
/// implementation from the detected version and refuses a version it cannot manage, so a caller
/// that holds an <see cref="IWhisparrAdapter"/> is already guaranteed a version this build supports.
/// </summary>
internal interface IWhisparrAdapter
{
    /// <summary>Whether this version supports adding an independent scene (a per-scene add/monitor).</summary>
    /// <remarks>
    /// v2 (Sonarr) has no <c>POST /episode</c> — a scene exists only under a site — so the orchestration seam
    /// must defer the per-scene add paths BEFORE resolving the origin tag / root folder, keeping a deferred v2
    /// add wire-free (no stray tag/root call). v3 (Eros) has a first-class movie add, so it is <c>true</c> there.
    /// </remarks>
    bool SupportsSceneAdd { get; }

    /// <summary>Whether this version can monitor the given entity <paramref name="kind"/>.</summary>
    /// <remarks>
    /// v2 maps a studio to a SITE but has NO performer entity, so a performer monitor must defer BEFORE the
    /// orchestration seam resolves the origin tag / root folder — otherwise a monitor-ON on a v2 performer that
    /// carries a TPDB id would create a stray <c>cove-sync</c> tag on the v2 host only to then refuse.
    /// </remarks>
    bool SupportsEntityMonitor(EntityKind kind);

    /// <summary>Whether this version can import a file Cove already owns in place (attach without moving/grabbing).</summary>
    /// <remarks>
    /// Both versions attach the on-disk file to an existing fileless scene without ever moving or deleting Cove's
    /// own file, and without grabbing. v3 (Eros) adopts the file in place by re-pointing the movie row's path to
    /// Cove's own scene folder + a rescan (zero duplication), falling back to a copy import only for a flat
    /// (shared-directory) layout; v2 registers a Sonarr episode in place. The file must sit under a Whisparr root
    /// (shared storage); the storage-alignment check surfaces a misaligned library before the action runs.
    /// </remarks>
    bool SupportsOwnedImport { get; }

    /// <summary>
    /// Imports a file Cove ALREADY OWNS into Whisparr, attaching it to <paramref name="scene"/> so Whisparr shows
    /// the scene as "have" — Cove's own file is NEVER moved or deleted, and it NEVER searches/grabs. The file must
    /// already sit where Whisparr can see it (shared storage), so <paramref name="whisparrFilePath"/> is the path
    /// AS WHISPARR SEES IT (the caller maps the Cove path first). <paramref name="mode"/> selects the v3 mechanism
    /// (the orchestration picks it from the library layout): <see cref="OwnedImportMode.InPlaceAdopt"/> re-points
    /// the movie row's path to Cove's folder + rescans (no duplication), <see cref="OwnedImportMode.Copy"/> is the
    /// flat-layout fallback. v2 ignores the mode (it always registers its episode in place).
    /// </summary>
    /// <remarks>
    /// <paramref name="scene"/> is the target scene from <see cref="ListMoviesAsync"/>: on v3 its <c>Id</c> is the
    /// Eros movie id; on v2 its <c>Id</c> is the episode id and <c>SeriesId</c> the enclosing site. The import is
    /// verified by re-reading the scene's has-file state; a queued-but-unlinked outcome is
    /// <see cref="WhisparrResultState.Unreachable"/>, never a false Ok.
    /// </remarks>
    Task<WhisparrResult<bool>> ImportOwnedSceneAsync(
        string baseUrl, string apiKey, WhisparrMovie scene, string whisparrFilePath, OwnedImportMode mode, CancellationToken ct);

    /// <summary>Reads the instance status (version + instance name) for the connect flow.</summary>
    Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>Lists the configured root folders (for the settings dropdown).</summary>
    Task<WhisparrResult<RootFolder[]>> ListRootFoldersAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>Lists the configured quality profiles (for the settings dropdown).</summary>
    Task<WhisparrResult<QualityProfile[]>> ListQualityProfilesAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>Lists the full Whisparr movie set (unpaged) — the reconciliation data source.</summary>
    Task<WhisparrResult<WhisparrMovie[]>> ListMoviesAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>Reads one newest-first page of Whisparr history — the polling-reconcile data source.</summary>
    Task<WhisparrResult<WhisparrHistoryPage>> ListHistoryAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Registers the Cove webhook connection for <paramref name="webhookUrl"/>. The adapter owns the
    /// version-specific notification payload shape (implementation / configContract / fields). Best-effort
    /// and single-shot: the underlying transport never blind-retries this non-idempotent call, and a non-2xx
    /// response is a non-Ok result the caller falls back on (copy-paste), never a thrown failure.
    /// </summary>
    Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string webhookUrl, CancellationToken ct);

    /// <summary>
    /// Sets a studio's or performer's monitor state via add-then-flip: if the
    /// entity is absent in Whisparr it is first created with <c>monitored:false</c> (carrying the origin
    /// <paramref name="tagIds"/> and the <paramref name="rootFolderPath"/> + <paramref name="qualityProfileId"/>),
    /// then a separate PUT sets <paramref name="monitored"/> to the requested state. A create that returns
    /// 409/exists is treated as success (re-read, never a duplicate); turning monitor OFF only PUTs
    /// <c>monitored:false</c> (no add, no delete). NEVER triggers a Whisparr search on add. The
    /// <paramref name="kind"/> selects the studio-vs-performer wire shape so both flows stay symmetric.
    /// <paramref name="scope"/> selects how far monitoring cascades: <see cref="MonitorScope.NewReleases"/>
    /// monitors the container for future scenes only; <see cref="MonitorScope.AllScenes"/> also marks the
    /// existing attributed scenes wanted (a bulk monitor toggle, still no search). Scope is ignored when
    /// turning monitor OFF.
    /// </summary>
    Task<WhisparrResult<EntityMonitorResult>> SetEntityMonitorAsync(
        string baseUrl,
        string apiKey,
        EntityKind kind,
        string stashId,
        bool monitored,
        MonitorScope scope,
        string rootFolderPath,
        int qualityProfileId,
        IReadOnlyList<int> tagIds,
        CancellationToken ct);

    /// <summary>
    /// Projects the quiet-status for a studio/performer: whether it is added + currently monitored,
    /// plus the "grabbed of total" counts computed ONLY from the adapter's existing Whisparr movie set
    /// (a studio is attributed by movie <c>studioTitle</c>, a performer by <c>performerForeignIds</c>) — it
    /// makes no StashDB call. An absent entity returns added:false / 0-of-0.
    /// </summary>
    Task<WhisparrResult<EntityStatus>> GetEntityStatusAsync(
        string baseUrl,
        string apiKey,
        EntityKind kind,
        string stashId,
        CancellationToken ct);

    /// <summary>
    /// Lists the Whisparr import-list exclusion set — the source of the scene "Excluded" state.
    /// v3 reads <c>GET /api/v3/exclusions</c>; v2 defers gracefully (classified
    /// <see cref="WhisparrResultState.VersionMismatch"/>, no wire call, no throw — unsupported on v2).
    /// </summary>
    Task<WhisparrResult<WhisparrExclusion[]>> ListExclusionsAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>
    /// Lists the on-demand indexer releases for one movie (count + the interactive picker's
    /// enriched rows). v3 reads <c>GET /api/v3/release?movieId={movieId}</c>; v2 defers gracefully (classified
    /// <see cref="WhisparrResultState.VersionMismatch"/>, no wire call, no throw — unsupported on v2).
    /// </summary>
    Task<WhisparrResult<WhisparrRelease[]>> GetReleasesAsync(string baseUrl, string apiKey, int movieId, CancellationToken ct);

    /// <summary>
    /// Adds a Whisparr import-list exclusion for a scene by its StashDB id: POSTs an exclusion body
    /// carrying the id as <c>foreignId</c> plus the display <paramref name="title"/>/<paramref name="year"/>.
    /// Idempotent — a duplicate (Whisparr's 409 or an "exists" 400 body) resolves to <c>Ok(true)</c>, never a
    /// duplicate row. This is NOT a grab-capable path: it issues no search/command. v3-only; v2 defers
    /// gracefully (<see cref="WhisparrResultState.VersionMismatch"/>, no wire call).
    /// </summary>
    Task<WhisparrResult<bool>> AddExclusionAsync(
        string baseUrl, string apiKey, string stashId, string? title, int? year, CancellationToken ct);

    /// <summary>
    /// Removes the import-list exclusion for a scene by its StashDB id. The exclusion's Whisparr id
    /// is resolved SERVER-SIDE by matching <paramref name="stashId"/> against the fetched exclusion list's
    /// <c>foreignId</c> — never a caller-supplied id — then DELETEd by that row's id. A scene with no
    /// matching exclusion is an idempotent <c>Ok(true)</c> no-op (nothing to remove). Issues no search/command.
    /// v3-only; v2 defers gracefully (<see cref="WhisparrResultState.VersionMismatch"/>, no wire call).
    /// </summary>
    Task<WhisparrResult<bool>> RemoveExclusionAsync(string baseUrl, string apiKey, string stashId, CancellationToken ct);

    /// <summary>
    /// Grabs one specific indexer release (interactive grab): POSTs the <paramref name="guid"/> +
    /// <paramref name="indexerId"/> pair plus the <paramref name="movieId"/> Whisparr needs to address the
    /// release. A 2xx is <c>Ok(true)</c>. A distinct single-shot grab verb — never fused into an
    /// add/exclusion path. v3-only; v2 defers gracefully
    /// (<see cref="WhisparrResultState.VersionMismatch"/>, no wire call).
    /// </summary>
    /// <remarks>
    /// The <paramref name="movieId"/> is REQUIRED: a release row from the interactive search carries
    /// <c>movieId:null</c>, and grabbing without it makes Whisparr answer 404 "Unable to find matching
    /// movie" (which the transport misclassifies as unreachable). The caller supplies the movie it resolved
    /// for the scene.
    /// </remarks>
    Task<WhisparrResult<bool>> GrabReleaseAsync(
        string baseUrl, string apiKey, string guid, int indexerId, int movieId, CancellationToken ct);

    /// <summary>
    /// Searches the given movies for a quality upgrade (batch / scene) by posting one
    /// <c>MoviesSearch</c> command — Whisparr grabs an upgrade ONLY when the movie is monitored and its profile
    /// cutoff is unmet, so eligibility is enforced server-side. Together with <see cref="SearchScenesAsync"/>
    /// this is one of only two grab-capable adapter paths; every add/monitor/exclusion path stays search-free.
    /// An empty <paramref name="movieIds"/> is an <see cref="WhisparrResultState.Ok"/> no-op issuing NO command.
    /// v3-only; v2 defers gracefully (<see cref="WhisparrResultState.VersionMismatch"/>, no wire call).
    /// </summary>
    Task<WhisparrResult<BulkActionResult>> SearchForUpgradesAsync(
        string baseUrl, string apiKey, IReadOnlyList<int> movieIds, CancellationToken ct);

    /// <summary>
    /// Adds a scene to Whisparr as a movie by its StashDB id (add / register-owned): POSTs a
    /// movie body carrying the id in BOTH <c>foreignId</c> and <c>stashId</c>, the caller-chosen
    /// <paramref name="monitored"/> flag, the <paramref name="rootFolderPath"/> +
    /// <paramref name="qualityProfileId"/>, the origin <paramref name="tagIds"/>, and
    /// <c>addOptions.searchForMovie</c> = <paramref name="searchForMovie"/>. CRITICAL loop-safety:
    /// <paramref name="searchForMovie"/> DEFAULTS to <c>false</c> so an add registers without grabbing — the
    /// flag exists only so the invariant is testable; no caller passes <c>true</c>. A 2xx returns the created
    /// movie (<c>Added:true</c>); an HTTP 409 (or a re-read that finds it) resolves to the existing row
    /// (<c>Added:false</c>) — idempotent, never a duplicate.
    /// </summary>
    Task<WhisparrResult<SceneActionResult>> AddSceneAsync(
        string baseUrl,
        string apiKey,
        string stashId,
        string? title,
        bool monitored,
        bool searchForMovie,
        string rootFolderPath,
        int qualityProfileId,
        IReadOnlyList<int> tagIds,
        CancellationToken ct);

    /// <summary>
    /// Sets a scene's monitor state via add-then-flip: GET the movie by <paramref name="stashId"/>;
    /// if absent and <paramref name="monitored"/> is <c>true</c> it first adds the movie <c>monitored:false</c>
    /// (with <c>searchForMovie:false</c> — never grabs on add) then PUTs <c>monitored:true</c>; if present it
    /// PUTs the requested state; if absent and <paramref name="monitored"/> is <c>false</c> it is a no-op
    /// success (nothing to unmonitor). NEVER triggers a search. The <paramref name="title"/>,
    /// <paramref name="rootFolderPath"/>, <paramref name="qualityProfileId"/> and <paramref name="tagIds"/>
    /// supply the add leg when the scene is not yet in Whisparr.
    /// </summary>
    Task<WhisparrResult<SceneActionResult>> SetSceneMonitorAsync(
        string baseUrl,
        string apiKey,
        string stashId,
        string? title,
        bool monitored,
        string rootFolderPath,
        int qualityProfileId,
        IReadOnlyList<int> tagIds,
        CancellationToken ct);

    /// <summary>
    /// Posts a single <c>MoviesSearch</c> command over <paramref name="movieIds"/> (search-now /
    /// search-all). This is the ONLY adapter method that can cause a grab — every add/
    /// monitor path is search-free. An empty <paramref name="movieIds"/> is an <see cref="WhisparrResultState.Ok"/>
    /// no-op that issues NO command.
    /// </summary>
    Task<WhisparrResult<BulkActionResult>> SearchScenesAsync(
        string baseUrl, string apiKey, IReadOnlyList<int> movieIds, CancellationToken ct);

    /// <summary>
    /// Reads the four file-affecting Whisparr toggles off the naming + media-management config singletons —
    /// the source of the config editor's current state. v3 reads <c>GET /api/v3/config/naming</c> +
    /// <c>/config/mediamanagement</c>; v2 defers gracefully (classified
    /// <see cref="WhisparrResultState.VersionMismatch"/>, no wire call — its Sonarr-shaped config uses different
    /// field names, so the editor is v3-only this release).
    /// </summary>
    Task<WhisparrResult<WhisparrFileSettings>> GetFileSettingsAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>
    /// Writes the four file-affecting toggles via read-modify-write: GET each config singleton, flip ONLY the
    /// booleans <paramref name="request"/> supplies (an absent field is left at Whisparr's current value), then
    /// PUT the COMPLETE object back. The config singletons are whole-object replaces, so the unknown fields must
    /// round-trip verbatim — the server builds the body from the GET result + the four whitelisted booleans and
    /// NEVER PUTs a client-supplied config object. A GET that is not Ok short-circuits before any PUT. Returns the
    /// resulting settings. v3-only; v2 defers gracefully (<see cref="WhisparrResultState.VersionMismatch"/>, no wire call).
    /// </summary>
    Task<WhisparrResult<WhisparrFileSettings>> EditFileSettingsAsync(
        string baseUrl, string apiKey, WhisparrFileSettingsRequest request, CancellationToken ct);

    /// <summary>
    /// Lists the Whisparr movie ids attributed to a studio (by <c>studioTitle</c>) or performer (by
    /// <c>performerForeignIds</c>) — the search-all input, computed ONLY from the already-fetched
    /// Whisparr movie set (the SAME attribution predicate the status uses), never a StashDB call. When
    /// <paramref name="monitoredOnly"/> is <c>true</c> the result is filtered to monitored movies. An absent
    /// entity returns an empty array.
    /// </summary>
    Task<WhisparrResult<int[]>> ListAttributedMovieIdsAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, bool monitoredOnly, CancellationToken ct);
}
