using System.Globalization;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Monitor;
using WhisparrSync.Push;

namespace WhisparrSync.Adapters;

/// <summary>
/// The Whisparr v2 (Sonarr-based "v2" branch) adapter: implements the same
/// <see cref="IWhisparrAdapter"/> port as <see cref="V3Adapter"/>, but over v2's content model. The five
/// connect-level calls (status/rootfolder/qualityprofile/history/register) are byte-identical envelopes on
/// v2 (verified live), so they delegate to the shared transport-only
/// <see cref="WhisparrClient"/> unchanged. The ONE substantive method is <see cref="ListMoviesAsync"/>: v2
/// has no <c>/movie</c> entity, so it walks <c>series → episode → episodefile</c> and synthesizes the
/// normalized <c>WhisparrMovie[]</c> the reused <c>IdentityMatcher</c> already understands.
/// </summary>
/// <remarks>
/// The notification-payload helpers (<see cref="BuildNotificationPayload"/> / <see cref="ExtractToken"/>)
/// are duplicated from <see cref="V3Adapter"/> rather than lifted to a shared type: v2's <c>/notification</c>
/// toggle set is a superset of v3's (<c>onDownload</c>/<c>onUpgrade</c>/<c>onRename</c> all valid — verified),
/// so the payload is identical. Keeping them separate avoids coupling the two adapters, so a change here
/// carries no regression risk to the live-verified v3 path.
/// </remarks>
internal sealed class V2Adapter(WhisparrClient client, TimeSpan? monitorSettleDelay = null) : IWhisparrAdapter
{
    // Create-path resilience: a v2 site CREATE queues an async series refresh (it fetches the episode list)
    // that rebuilds the row AFTER the flip PUT and resets `monitored` back to false — the same shape as v3's
    // post-create RefreshStudios quirk (verified live: the flip's 202 is not authoritative on a fresh add). So a
    // fresh create verifies + re-asserts `monitored` until a read-back confirms it, bounded by the attempt
    // budget. An existing-site flip triggers no refresh, so it is authoritative and skips the verify.
    private const int MonitorVerifyMaxAttempts = 3;

    // The bounded episode re-read budget after a ManualImport: the command completes async, so the owned-import
    // path settles + re-reads the episode's hasFile a few times before concluding the file did not link.
    private const int ImportVerifyMaxAttempts = 4;

    // The per-attempt settle before the verify read-back — long enough for an in-flight refresh to land
    // (TimeSpan.Zero in tests, which exercises the re-assert LOGIC without sleeping).
    private static readonly TimeSpan DefaultMonitorSettleDelay = TimeSpan.FromSeconds(1.5);
    private readonly TimeSpan _monitorSettleDelay = monitorSettleDelay ?? DefaultMonitorSettleDelay;

    // v2 has no POST /episode: a scene is acquired by adding its site and searching the episode, never a
    // standalone per-scene add. The orchestration seam reads this to defer the add paths before any wire call.
    public bool SupportsSceneAdd => false;

    // Only a studio maps to a v2 SITE; there is no v2 performer entity, so a performer monitor defers.
    public bool SupportsEntityMonitor(EntityKind kind) => kind == EntityKind.Studio;

    // v2 imports an owned file in place via a targeted ManualImport (attach to a specific episode, no move/grab).
    public bool SupportsOwnedImport => true;

    public Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.GetStatusAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<RootFolder[]>> ListRootFoldersAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.ListRootFoldersAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<QualityProfile[]>> ListQualityProfilesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.ListQualityProfilesAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<WhisparrHistoryPage>> ListHistoryAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct)
        => client.ListHistoryAsync(baseUrl, apiKey, page, pageSize, ct);

    public Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string webhookUrl, CancellationToken ct)
        => client.RegisterWebhookAsync(baseUrl, apiKey, BuildNotificationPayload(webhookUrl), ct);

    // A Cove studio maps to a v2 SITE (series); a performer has no v2 analog. On v2 the stashId parameter
    // carries the TPDB site id (the resolution rule that fills it lives elsewhere), matched against the site's
    // tvdbId slot — v2 rows carry no StashDB id, so this is how the outward target is resolved.
    public Task<WhisparrResult<EntityMonitorResult>> SetEntityMonitorAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, bool monitored, MonitorScope scope,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
        => kind == EntityKind.Studio
            ? SetSiteMonitorAsync(baseUrl, apiKey, stashId, monitored, scope, rootFolderPath, qualityProfileId, tagIds, ct)
            // DEFER: v2 has NO performer entity — performers are embedded episode.actors[] metadata with no
            // monitorable resource, so there is nothing to add or flip.
            : Task.FromResult(WhisparrResult<EntityMonitorResult>.VersionMismatch("v2"));

    public Task<WhisparrResult<EntityStatus>> GetEntityStatusAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, CancellationToken ct)
        => kind == EntityKind.Studio
            ? GetSiteStatusAsync(baseUrl, apiKey, stashId, ct)
            // DEFER: no v2 performer entity to report a status for (see SetEntityMonitorAsync).
            : Task.FromResult(WhisparrResult<EntityStatus>.VersionMismatch("v2"));

    /// <summary>
    /// v2 exclusion reads are unsupported. Returns a graceful classified
    /// <see cref="WhisparrResultState.VersionMismatch"/> ("v2") — never a throw and never a v3 call — so the
    /// scene-status caller resolves every scene to <c>NotAdded</c>/unknown rather than a wrong-version read.
    /// v2's exclusion surface differs from v3's <c>/api/v3/exclusions</c>, so there is no correct read here yet.
    /// </summary>
    public Task<WhisparrResult<WhisparrExclusion[]>> ListExclusionsAsync(string baseUrl, string apiKey, CancellationToken ct)
        => Task.FromResult(WhisparrResult<WhisparrExclusion[]>.VersionMismatch("v2"));

    /// <summary>
    /// v2 indexer-release reads are unsupported. Mirrors <see cref="ListExclusionsAsync"/>: a graceful
    /// <see cref="WhisparrResultState.VersionMismatch"/> ("v2"), never a throw or a v3 call.
    /// </summary>
    public Task<WhisparrResult<WhisparrRelease[]>> GetReleasesAsync(string baseUrl, string apiKey, int movieId, CancellationToken ct)
        => Task.FromResult(WhisparrResult<WhisparrRelease[]>.VersionMismatch("v2"));

    /// <summary>
    /// v3-only exclusion write: DEFERRED on v2. Returns a graceful classified
    /// <see cref="WhisparrResultState.VersionMismatch"/> ("v2") with NO wire call — v2's exclusion surface
    /// differs from v3's <c>/api/v3/exclusions</c>, so there is no correct write here.
    /// </summary>
    public Task<WhisparrResult<bool>> AddExclusionAsync(
        string baseUrl, string apiKey, string stashId, string? title, int? year, CancellationToken ct)
        => Task.FromResult(WhisparrResult<bool>.VersionMismatch("v2"));

    /// <summary>
    /// v3-only un-exclude: DEFERRED on v2. Mirrors <see cref="AddExclusionAsync"/> — a graceful
    /// <see cref="WhisparrResultState.VersionMismatch"/> ("v2"), never a throw or a v3 call.
    /// </summary>
    public Task<WhisparrResult<bool>> RemoveExclusionAsync(string baseUrl, string apiKey, string stashId, CancellationToken ct)
        => Task.FromResult(WhisparrResult<bool>.VersionMismatch("v2"));

    /// <summary>
    /// v3-only interactive grab: DEFERRED on v2. A graceful
    /// <see cref="WhisparrResultState.VersionMismatch"/> ("v2") with NO wire call — so no v2 path can grab a release.
    /// </summary>
    public Task<WhisparrResult<bool>> GrabReleaseAsync(
        string baseUrl, string apiKey, string guid, int indexerId, int movieId, CancellationToken ct)
        => Task.FromResult(WhisparrResult<bool>.VersionMismatch("v2"));

    /// <summary>
    /// DEFERRED on v2: a graceful <see cref="WhisparrResultState.VersionMismatch"/> ("v2") with NO wire call.
    /// v2 (Sonarr) has no cutoff-upgrade-only search variant — the sole grab-capable v2 verb is the episode
    /// search (<see cref="SearchScenesAsync"/>), so an upgrade search has no distinct v2 command.
    /// </summary>
    public Task<WhisparrResult<BulkActionResult>> SearchForUpgradesAsync(
        string baseUrl, string apiKey, IReadOnlyList<int> movieIds, CancellationToken ct)
        => Task.FromResult(WhisparrResult<BulkActionResult>.VersionMismatch("v2"));

    /// <summary>
    /// DEFERRED on v2: a graceful <see cref="WhisparrResultState.VersionMismatch"/> ("v2") with NO wire call.
    /// v2 has no per-scene add — there is no <c>POST /episode</c>; episodes exist only under a series, so a
    /// scene is acquired by adding its site (<see cref="SetEntityMonitorAsync"/>) and searching the episode
    /// (<see cref="SearchScenesAsync"/>).
    /// </summary>
    public Task<WhisparrResult<SceneActionResult>> AddSceneAsync(
        string baseUrl, string apiKey, string stashId, string? title, bool monitored, bool searchForMovie,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
        => Task.FromResult(WhisparrResult<SceneActionResult>.VersionMismatch("v2"));

    /// <summary>
    /// DEFERRED on v2: a graceful <see cref="WhisparrResultState.VersionMismatch"/> ("v2") with NO wire call.
    /// The add-then-flip add leg has no v2 per-scene add (see <see cref="AddSceneAsync"/>), so an absent scene
    /// cannot be registered to then flip its monitor state.
    /// </summary>
    public Task<WhisparrResult<SceneActionResult>> SetSceneMonitorAsync(
        string baseUrl, string apiKey, string stashId, string? title, bool monitored,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
        => Task.FromResult(WhisparrResult<SceneActionResult>.VersionMismatch("v2"));

    /// <summary>
    /// v3-only config read: DEFERRED on v2. A graceful classified <see cref="WhisparrResultState.VersionMismatch"/>
    /// ("v2") with NO wire call — v2 (Sonarr-based) exposes the same <c>/config/*</c> paths but with divergent
    /// Sonarr field names (<c>renameEpisodes</c>, series-format), so the file-settings editor is v3-only this release.
    /// </summary>
    public Task<WhisparrResult<WhisparrFileSettings>> GetFileSettingsAsync(string baseUrl, string apiKey, CancellationToken ct)
        => Task.FromResult(WhisparrResult<WhisparrFileSettings>.VersionMismatch("v2"));

    /// <summary>
    /// v3-only config write: DEFERRED on v2. Mirrors <see cref="GetFileSettingsAsync"/> — a graceful
    /// <see cref="WhisparrResultState.VersionMismatch"/> ("v2") with NO wire call (no GET, no PUT), so a v2
    /// instance can never have its Sonarr-shaped config written through the v3 field map.
    /// </summary>
    public Task<WhisparrResult<WhisparrFileSettings>> EditFileSettingsAsync(
        string baseUrl, string apiKey, WhisparrFileSettingsRequest request, CancellationToken ct)
        => Task.FromResult(WhisparrResult<WhisparrFileSettings>.VersionMismatch("v2"));

    // Episode search: the ONLY grab-capable v2 verb (EpisodeSearch over the given episode ids, reusing the
    // shared command transport). An empty id set issues NO command (Ok no-op), mirroring V3Adapter.
    public async Task<WhisparrResult<BulkActionResult>> SearchScenesAsync(
        string baseUrl, string apiKey, IReadOnlyList<int> movieIds, CancellationToken ct)
    {
        if (movieIds.Count == 0)
        {
            return WhisparrResult<BulkActionResult>.Ok(BulkActionResult.Empty);
        }

        var body = JsonSerializer.Serialize(new { name = "EpisodeSearch", episodeIds = movieIds });
        var result = await client.SendCommandAsync(baseUrl, apiKey, body, ct);
        return result.IsOk
            ? WhisparrResult<BulkActionResult>.Ok(new BulkActionResult(movieIds.Count, movieIds.Count, Failed: 0))
            : Propagate<bool, BulkActionResult>(result);
    }

    /// <summary>
    /// Imports a Cove-owned file IN PLACE and attaches it to the <paramref name="scene"/> episode under its
    /// enclosing site via a targeted <c>ManualImport</c> (verified live): the file must already sit under the
    /// site's folder in Whisparr's view (shared storage), so Whisparr registers it where it is — no move, copy,
    /// or grab. Never issues a search (<c>importMode:"auto"</c> is moot for an in-place file).
    /// </summary>
    /// <remarks>
    /// <paramref name="scene"/> is a synthesized v2 scene (<c>Id</c> = episode id, <c>SeriesId</c> = the site);
    /// <c>SeriesId</c> is required (a v2 owned-import always has an enclosing site). The quality + languages MUST
    /// come from the <c>manualimport</c> listing verbatim (a synthesized quality does not import); the row is
    /// matched to the owned file by path (separator/case-normalized). The listing may carry a name-parse rejection
    /// ("Invalid season or episode") — IGNORED here because the explicit <c>episodeIds</c> override it. The command
    /// completes async, so success is confirmed by re-reading the episode's <c>hasFile</c>; a queued-but-unlinked
    /// outcome is Unreachable, never a false Ok.
    /// </remarks>
    public async Task<WhisparrResult<bool>> ImportOwnedSceneAsync(
        string baseUrl, string apiKey, WhisparrMovie scene, string whisparrFilePath, OwnedImportMode mode, CancellationToken ct)
    {
        // A v2 scene is a Sonarr episode attached under its enclosing series folder, so the import is always
        // in place — the adopt-vs-copy mode is a v3 (per-movie-folder) concern and does not apply here.
        _ = mode;

        if (scene.SeriesId is not { } seriesId)
        {
            return WhisparrResult<bool>.Unreachable("v2 owned-import requires an enclosing site (SeriesId)");
        }

        var episodeId = scene.Id;
        var normalizedPath = whisparrFilePath.Replace('\\', '/');
        var lastSlash = normalizedPath.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return WhisparrResult<bool>.Unreachable($"cannot derive folder from '{whisparrFilePath}'");
        }

        var folder = normalizedPath[..lastSlash];
        var listResult = await client.ListManualImportAsync(baseUrl, apiKey, folder, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrManualImportItem[], bool>(listResult);
        }

        var row = Array.Find(
            listResult.Value!,
            i => i.Path is not null
                && string.Equals(i.Path.Replace('\\', '/'), normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return WhisparrResult<bool>.Unreachable($"Whisparr did not list '{whisparrFilePath}'");
        }

        // The path echoed back is the row's own path (the file exactly as Whisparr sees it), and the
        // quality/languages are the listed objects verbatim — a synthesized quality does not import.
        var body = JsonSerializer.Serialize(new
        {
            name = "ManualImport",
            importMode = "auto",
            files = new[]
            {
                new
                {
                    path = row.Path,
                    seriesId,
                    episodeIds = new[] { episodeId },
                    quality = row.Quality,
                    languages = row.Languages,
                    releaseGroup = "",
                },
            },
        });

        var command = await client.SendCommandAsync(baseUrl, apiKey, body, ct);
        if (!command.IsOk)
        {
            return Propagate<bool, bool>(command);
        }

        for (var attempt = 1; attempt <= ImportVerifyMaxAttempts; attempt++)
        {
            await Task.Delay(_monitorSettleDelay, ct);

            var episodesResult = await client.ListEpisodesAsync(baseUrl, apiKey, seriesId, ct);
            if (!episodesResult.IsOk)
            {
                return Propagate<WhisparrEpisode[], bool>(episodesResult);
            }

            if (Array.Find(episodesResult.Value!, e => e.Id == episodeId) is { HasFile: true })
            {
                return WhisparrResult<bool>.Ok(true);
            }
        }

        return WhisparrResult<bool>.Unreachable("ManualImport queued but episode not linked");
    }

    // The search-all input: the site's episode ids (the v2 analogue of the v3 attributed-movie set). A
    // studio resolves to its site by TPDB id, then projects its episode ids (optionally monitored). An absent
    // site is an empty array; a performer DEFERs (no v2 performer entity). Unlike the status count (which
    // degrades a failed episode read to 0-of-0), a bulk search MUST act on the real set, so a non-Ok episode
    // read propagates rather than searching a partial set.
    public async Task<WhisparrResult<int[]>> ListAttributedMovieIdsAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, bool monitoredOnly, CancellationToken ct)
    {
        if (kind != EntityKind.Studio)
        {
            return WhisparrResult<int[]>.VersionMismatch("v2");
        }

        var listResult = await client.ListSeriesAsync(baseUrl, apiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrSeries[], int[]>(listResult);
        }

        var series = FindByTpdb(listResult.Value!, stashId);
        if (series is null)
        {
            return WhisparrResult<int[]>.Ok([]);
        }

        var episodesResult = await client.ListEpisodesAsync(baseUrl, apiKey, series.Id, ct);
        if (!episodesResult.IsOk)
        {
            return Propagate<WhisparrEpisode[], int[]>(episodesResult);
        }

        var ids = episodesResult.Value!
            .Where(e => !monitoredOnly || e.Monitored)
            .Select(e => e.Id)
            .ToArray();
        return WhisparrResult<int[]>.Ok(ids);
    }

    /// <summary>
    /// The v2 scene-enumeration remap (the reconciliation data source): <c>GET /series</c>, then per series
    /// <c>GET /episode?seriesId</c> + <c>GET /episodefile?seriesId</c>, synthesizing one
    /// <see cref="WhisparrMovie"/> per episode. CRITICAL: <c>StashId</c> is
    /// null and <c>ItemType</c> is <c>"v2scene"</c> (never <c>"scene"</c>) so <c>IdentityMatcher.StashMatches</c>
    /// no-ops for v2 rows — a TPDB id is never compared to a Cove StashDB UUID. Fail-safe: if the series read
    /// (or any per-series episode/episodefile read) is not Ok, the same-state result propagates — no partial synth.
    /// </summary>
    public async Task<WhisparrResult<WhisparrMovie[]>> ListMoviesAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var seriesResult = await client.ListSeriesAsync(baseUrl, apiKey, ct);
        if (!seriesResult.IsOk)
        {
            return Propagate<WhisparrSeries[], WhisparrMovie[]>(seriesResult);
        }

        var movies = new List<WhisparrMovie>();
        foreach (var series in seriesResult.Value!)
        {
            var episodesResult = await client.ListEpisodesAsync(baseUrl, apiKey, series.Id, ct);
            if (!episodesResult.IsOk)
            {
                return Propagate<WhisparrEpisode[], WhisparrMovie[]>(episodesResult);
            }

            var filesResult = await client.ListEpisodeFilesAsync(baseUrl, apiKey, series.Id, ct);
            if (!filesResult.IsOk)
            {
                return Propagate<WhisparrEpisodeFile[], WhisparrMovie[]>(filesResult);
            }

            // Join episode.episodeFileId -> episodefile.path. Last row wins on a duplicate id (Whisparr
            // returns one file per id), and an episode whose file is absent yields MovieFile = null.
            var pathByFileId = new Dictionary<int, string?>();
            foreach (var file in filesResult.Value!)
            {
                pathByFileId[file.Id] = file.Path;
            }

            foreach (var episode in episodesResult.Value!)
            {
                WhisparrMovieFile? movieFile = null;
                if (episode.EpisodeFileId != 0 && pathByFileId.TryGetValue(episode.EpisodeFileId, out var path))
                {
                    movieFile = new WhisparrMovieFile(episode.EpisodeFileId, path);
                }

                movies.Add(new WhisparrMovie(
                    Id: episode.Id,
                    Title: episode.Title,
                    Year: ParseYear(episode.ReleaseDate),
                    StashId: null,
                    ForeignId: episode.TvdbId?.ToString(CultureInfo.InvariantCulture),
                    ItemType: "v2scene",
                    Monitored: episode.Monitored,
                    HasFile: episode.HasFile,
                    MovieFile: movieFile,
                    // The enclosing series is the site the owned-import ManualImport targets (a v2 scene =
                    // a Sonarr episode under this series); v3 movie rows leave this null.
                    SeriesId: series.Id));
            }
        }

        return WhisparrResult<WhisparrMovie[]>.Ok([.. movies]);
    }

    // Site (series) add-then-flip, mirroring V3Adapter.SetStudioMonitorAsync over v2's SITE model. GET /series
    // (matched by the TPDB id in tvdbId) answers whether the site is already added. Absent + ON: look the
    // addable row up (tpdb:{id}), add it monitored:false NON-grabbing, then PUT the requested state; a duplicate
    // add (400 SeriesExistsValidator = Conflict) re-reads the existing row (never a second create). Absent + OFF
    // is a no-op (nothing to unmonitor). v2 has no post-create refresh (the v3 RefreshStudios quirk), so the PUT
    // is authoritative — no verify loop.
    //
    // Decision — v2 gets NO Cove-owned refresh-on-monitor population step (unlike V3Adapter.PopulateCatalogueAsync):
    // the v3 flood this phase fixes is Whisparr Eros's OWN post-monitor RefreshStudios leaving the discovered
    // back-catalogue HARD-CODED monitored regardless of the requested scope. Sonarr (v2) has no such quirk — it
    // HONORS the series add's addOptions.monitor lever, so NewReleases ("none") leaves the back-catalogue
    // unmonitored and AllScenes ("all") wants it, with monitorNewItems:"all" acquiring future episodes either way
    // (see BuildSiteAddBody). The scope intent is already correct at add time, so there is nothing to reconcile
    // and no analog to fix. Adding a speculative v2 refresh/episode-editor population step would be an UNVERIFIED
    // behavior change that risks arming a grab, so v2 stays on its existing add-body lever this phase. Loop-safety
    // parity: this path issues NO /command (no RefreshSeries, no episode search) — asserted by V2OutwardParityTests.
    private async Task<WhisparrResult<EntityMonitorResult>> SetSiteMonitorAsync(
        string baseUrl, string apiKey, string tpdbId, bool monitored, MonitorScope scope,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var listResult = await client.ListSeriesAsync(baseUrl, apiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrSeries[], EntityMonitorResult>(listResult);
        }

        var existing = FindByTpdb(listResult.Value!, tpdbId);
        var added = false;

        if (existing is null)
        {
            if (!monitored)
            {
                return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(Added: false, Monitored: false));
            }

            var lookupResult = await client.LookupSeriesAsync(baseUrl, apiKey, $"tpdb:{tpdbId}", ct);
            if (!lookupResult.IsOk)
            {
                return Propagate<WhisparrSeries[], EntityMonitorResult>(lookupResult);
            }

            var addable = FindByTpdb(lookupResult.Value!, tpdbId);
            if (addable is null)
            {
                return WhisparrResult<EntityMonitorResult>.Unreachable($"no v2 site matches tpdb:{tpdbId}");
            }

            var createResult = await client.CreateSeriesAsync(
                baseUrl, apiKey, BuildSiteAddBody(addable, tpdbId, scope, rootFolderPath, qualityProfileId, tagIds), ct);

            if (createResult.State == WhisparrResultState.Conflict)
            {
                var reread = await client.ListSeriesAsync(baseUrl, apiKey, ct);
                if (!reread.IsOk)
                {
                    return Propagate<WhisparrSeries[], EntityMonitorResult>(reread);
                }

                existing = FindByTpdb(reread.Value!, tpdbId);
                if (existing is null)
                {
                    return WhisparrResult<EntityMonitorResult>.Unreachable("series create conflicted but no row on re-read");
                }
            }
            else if (createResult.IsOk)
            {
                existing = createResult.Value!;
                added = true;
            }
            else
            {
                return Propagate<WhisparrSeries, EntityMonitorResult>(createResult);
            }
        }
        else if (!monitored && !existing.Monitored)
        {
            return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(added, Monitored: false));
        }

        var putResult = await client.UpdateSeriesAsync(
            baseUrl, apiKey, existing.Id, BuildSiteFlipBody(existing, monitored, rootFolderPath, qualityProfileId, tagIds), ct);
        if (!putResult.IsOk)
        {
            return Propagate<WhisparrSeries, EntityMonitorResult>(putResult);
        }

        var cascade = await CascadeEpisodeMonitorAsync(baseUrl, apiKey, existing.Id, monitored, scope, ct);
        if (!cascade.IsOk)
        {
            return Propagate<bool, EntityMonitorResult>(cascade);
        }

        // A fresh create's async refresh can revert the site `monitored` after the flip — verify + re-assert
        // until it sticks, returning the VERIFIED read (honest even if it never stuck). An existing-site flip
        // (added:false) or an unmonitor needs no verify: no refresh runs, so the flip is durable truth.
        if (added && monitored)
        {
            var verified = await VerifySiteMonitoredAsync(
                baseUrl, apiKey, existing.Id, rootFolderPath, qualityProfileId, tagIds, ct);
            return verified.IsOk
                ? WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(added, verified.Value))
                : Propagate<bool, EntityMonitorResult>(verified);
        }

        return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(added, monitored));
    }

    // Create-path monitor verify: after the flip on a freshly-created site, v2's async refresh can reset
    // `monitored`. Settle, re-read the site (list + find by id — v2 has no by-id GET verb and few sites), and if
    // it reverted re-assert the flip (idempotent, still no search), repeating up to the attempt budget. Returns
    // the last VERIFIED read so `/monitor` and the UI reflect durable truth, mirroring V3Adapter's studio verify.
    private async Task<WhisparrResult<bool>> VerifySiteMonitoredAsync(
        string baseUrl, string apiKey, int seriesId, string rootFolderPath, int qualityProfileId,
        IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var observed = true;
        for (var attempt = 1; attempt <= MonitorVerifyMaxAttempts; attempt++)
        {
            await Task.Delay(_monitorSettleDelay, ct);

            var reread = await client.ListSeriesAsync(baseUrl, apiKey, ct);
            if (!reread.IsOk)
            {
                return Propagate<WhisparrSeries[], bool>(reread);
            }

            var row = Array.Find(reread.Value!, s => s.Id == seriesId);
            if (row is null)
            {
                return WhisparrResult<bool>.Unreachable("site vanished during monitor verify");
            }

            observed = row.Monitored;
            if (observed)
            {
                return WhisparrResult<bool>.Ok(true);
            }

            // Reverted by the refresh — re-assert, then loop to re-verify. Skip the re-PUT on the final attempt
            // (an unverifiable re-PUT is pointless); report what Whisparr actually holds.
            if (attempt < MonitorVerifyMaxAttempts)
            {
                var reput = await client.UpdateSeriesAsync(
                    baseUrl, apiKey, row.Id,
                    BuildSiteFlipBody(row, monitored: true, rootFolderPath, qualityProfileId, tagIds), ct);
                if (!reput.IsOk)
                {
                    return Propagate<WhisparrSeries, bool>(reput);
                }
            }
        }

        return WhisparrResult<bool>.Ok(observed);
    }

    // Apply the AllScenes scope to the site's existing episodes (scenes) via the bulk episode-monitor toggle,
    // which never searches — loop-safety holds. Fires only when turning monitoring ON with AllScenes;
    // NewReleases and OFF are no-ops (the add body's monitorNewItems:"all" acquires future episodes, and — like
    // Whisparr's own unmonitor — turning the site off leaves the episode monitored flags alone). A fresh site
    // may have no episodes yet (Whisparr fetches them asynchronously), so this cascades over whatever episodes
    // already exist; the add body's monitor:"all" carries the intent for episodes that arrive later.
    private async Task<WhisparrResult<bool>> CascadeEpisodeMonitorAsync(
        string baseUrl, string apiKey, int seriesId, bool monitored, MonitorScope scope, CancellationToken ct)
    {
        if (!monitored || scope != MonitorScope.AllScenes)
        {
            return WhisparrResult<bool>.Ok(true);
        }

        var episodesResult = await client.ListEpisodesAsync(baseUrl, apiKey, seriesId, ct);
        if (!episodesResult.IsOk)
        {
            return Propagate<WhisparrEpisode[], bool>(episodesResult);
        }

        var ids = episodesResult.Value!.Select(e => e.Id).ToArray();
        if (ids.Length == 0)
        {
            return WhisparrResult<bool>.Ok(true);
        }

        var body = JsonSerializer.Serialize(new { episodeIds = ids, monitored = true });
        return await client.MonitorEpisodesAsync(baseUrl, apiKey, body, ct);
    }

    // Site status: resolve the site by its TPDB id, then count grabbed (hasFile) of total from its episodes.
    // An absent site is added:false / 0-of-0; a failed episode read degrades to 0-of-0 (never a misleading count).
    private async Task<WhisparrResult<EntityStatus>> GetSiteStatusAsync(
        string baseUrl, string apiKey, string tpdbId, CancellationToken ct)
    {
        var listResult = await client.ListSeriesAsync(baseUrl, apiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrSeries[], EntityStatus>(listResult);
        }

        var series = FindByTpdb(listResult.Value!, tpdbId);
        if (series is null)
        {
            return WhisparrResult<EntityStatus>.Ok(new EntityStatus(Added: false, Monitored: false, ScenesPresent: 0, ScenesTotal: 0));
        }

        var episodesResult = await client.ListEpisodesAsync(baseUrl, apiKey, series.Id, ct);
        if (!episodesResult.IsOk)
        {
            return WhisparrResult<EntityStatus>.Ok(new EntityStatus(Added: true, series.Monitored, ScenesPresent: 0, ScenesTotal: 0));
        }

        var episodes = episodesResult.Value!;
        return WhisparrResult<EntityStatus>.Ok(
            new EntityStatus(Added: true, series.Monitored, episodes.Count(e => e.HasFile), episodes.Length));
    }

    /// <summary>
    /// Studio status for MANY sites from the PRE-FETCHED series list — the v2 analog of
    /// <see cref="V3Adapter.ClassifyEntityStatusBatch"/>. Each Cove studio's ThePornDB id is matched to a site
    /// by <c>tvdbId</c> (v2's TPDB slot); the count is the site's own <c>statistics</c> off the list row
    /// (episodes-with-file / full catalog), so a page of studios costs ONE series-list call, no per-site episode
    /// fetch. An id with no matching site is added:false. Studio-only — v2 has no performer entity.
    /// </summary>
    public static IReadOnlyDictionary<string, EntityStatus> ClassifyStudioStatusBatch(
        IReadOnlyList<string> tpdbIds, WhisparrSeries[] series)
    {
        var byTpdb = new Dictionary<string, WhisparrSeries>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in series)
        {
            if (site.TvdbId is { } id)
            {
                var key = id.ToString(CultureInfo.InvariantCulture);
                if (!byTpdb.ContainsKey(key))
                {
                    byTpdb[key] = site;
                }
            }
        }

        var result = new Dictionary<string, EntityStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var tpdbId in tpdbIds)
        {
            result[tpdbId] = byTpdb.TryGetValue(tpdbId, out var site)
                ? new EntityStatus(Added: true, site.Monitored,
                    site.Statistics?.EpisodeFileCount ?? 0, site.Statistics?.TotalEpisodeCount ?? 0)
                : new EntityStatus(Added: false, Monitored: false, ScenesPresent: 0, ScenesTotal: 0);
        }

        return result;
    }

    // A site row resolved by its TPDB id (Sonarr's tvdbId slot). v2 carries no StashDB id, so a Cove studio
    // maps to a v2 site by TPDB where the v3 studio path resolves by StashDB foreignId.
    private static WhisparrSeries? FindByTpdb(WhisparrSeries[] series, string tpdbId)
        => int.TryParse(tpdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? Array.Find(series, s => s.TvdbId == id)
            : null;

    // The v2 SITE ADD body: the addable lookup row's identity plus the caller's root/profile/origin tags.
    // monitored is false (a separate PUT sets the target). The scope selects addOptions.monitor: AllScenes →
    // "all" (every existing episode wanted), NewReleases → "none" (back-catalogue left alone); monitorNewItems
    // is "all" either way so future episodes are acquired. CRITICAL (loop-safety): searchForMissingEpisodes is
    // false regardless of scope — the add REGISTERS the site and marks episodes WANTED without grabbing; only
    // an explicit episode search grabs.
    private static string BuildSiteAddBody(
        WhisparrSeries addable, string tpdbId, MonitorScope scope, string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds)
        => JsonSerializer.Serialize(new
        {
            tvdbId = addable.TvdbId ?? ParseTpdb(tpdbId),
            title = addable.Title,
            titleSlug = addable.TitleSlug,
            qualityProfileId,
            rootFolderPath,
            monitored = false,
            monitorNewItems = "all",
            seasonFolder = true,
            tags = tagIds,
            addOptions = new
            {
                monitor = scope == MonitorScope.AllScenes ? "all" : "none",
                searchForMissingEpisodes = false,
                searchForCutoffUnmetEpisodes = false,
            },
        });

    // The v2 SITE FLIP body: echo the site resource with monitored set to the target. CRITICAL: NO addOptions —
    // a flip never searches. monitorNewItems follows the target state (acquire future episodes while monitored,
    // stop when off). Preserves tvdbId/title/root/profile/tags so the toggle never re-routes the site or drops
    // the origin tag.
    private static string BuildSiteFlipBody(
        WhisparrSeries series, bool monitored, string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds)
        => JsonSerializer.Serialize(new
        {
            id = series.Id,
            tvdbId = series.TvdbId,
            title = series.Title,
            titleSlug = series.TitleSlug,
            path = series.Path,
            qualityProfileId = series.QualityProfileId ?? qualityProfileId,
            rootFolderPath = series.RootFolderPath ?? rootFolderPath,
            monitored,
            monitorNewItems = monitored ? "all" : "none",
            tags = series.Tags ?? [.. tagIds],
        });

    // The TPDB id parsed to the int tvdbId slot; 0 when non-numeric (a defensive fallback — the addable
    // lookup row's own tvdbId is used first).
    private static int ParseTpdb(string tpdbId)
        => int.TryParse(tpdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    // Take the leading 4-digit year of an ISO-ish releaseDate ("2016-06-13" -> 2016); null on absence or
    // a non-numeric leading segment (defensive — a partial/odd row must never throw the whole synth).
    private static int? ParseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate) || releaseDate.Length < 4)
        {
            return null;
        }

        return int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    // Re-shape a non-Ok result of one payload type into the same state for another — the synth propagates a
    // failed read verbatim (state + diagnostic) rather than inventing a partial success.
    private static WhisparrResult<TTo> Propagate<TFrom, TTo>(WhisparrResult<TFrom> source)
        => WhisparrResult<TTo>.PropagateFrom(source);

    // The v2 Webhook connection payload — identical to v3's (the toggle set is a superset on v2). See the
    // class <remarks> for why this is duplicated rather than shared. `method` value 1 = POST; the secret is
    // delivered as the `X-Cove-Token` header so the receiver authenticates the Test ping.
    internal static string BuildNotificationPayload(string webhookUrl)
        => JsonSerializer.Serialize(new
        {
            name = "Cove Whisparr Sync",
            implementation = "Webhook",
            implementationName = "Webhook",
            configContract = "WebhookSettings",
            onGrab = false,
            onDownload = true,
            onUpgrade = true,
            onRename = true,
            fields = new object[]
            {
                new { name = "url", value = webhookUrl },
                new { name = "method", value = 1 },
                new { name = "headers", value = new object[] { new { key = "X-Cove-Token", value = ExtractToken(webhookUrl) } } },
            },
        });

    // Lift the `?token=` query value back out so the header carries the identical secret the receiver
    // validates. Returns empty when no token query is present.
    private static string ExtractToken(string webhookUrl)
    {
        const string marker = "?token=";
        var start = webhookUrl.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var raw = webhookUrl[(start + marker.Length)..];
        var amp = raw.IndexOf('&');
        if (amp >= 0)
        {
            raw = raw[..amp];
        }

        return Uri.UnescapeDataString(raw);
    }
}
