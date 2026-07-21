using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Monitor;
using WhisparrSync.Push;

namespace WhisparrSync.Adapters;

/// <summary>
/// The Whisparr v3 ("Eros") adapter: implements the connect flow against the <c>/api/v3</c>
/// surface by delegating each call to the transport-only <see cref="WhisparrClient"/>. All v3-specific
/// wire knowledge (which client endpoint answers each port method) lives here, not in the handlers — so a
/// future v2 adapter slots behind the same <see cref="IWhisparrAdapter"/> port without touching callers.
/// </summary>
internal sealed class V3Adapter(WhisparrClient client, TimeSpan? monitorSettleDelay = null) : IWhisparrAdapter
{
    // Studio create-path resilience: Whisparr Eros queues a `RefreshStudios` command on studio CREATE
    // that rebuilds the row AFTER our flip PUT and resets `monitored` back to false within a few seconds
    // (verified live). So on the fresh-create path we re-read after a short settle and re-assert the
    // requested state, bounded, until a GET confirms it (or the budget is spent). The already-exists / 409
    // path triggers no refresh, so it is left untouched. Performers have no equivalent post-create refresh
    // (no RefreshPerformers — verified live), so their path is deliberately not burdened with the settle.
    private const int MonitorVerifyMaxAttempts = 3;

    // The per-attempt settle before a verify read-back — long enough for an in-flight RefreshStudios to land,
    // short enough to keep the toggle responsive. Overridable (tests pass TimeSpan.Zero to exercise the
    // re-assert logic without real waiting); defaults to the production value.
    private static readonly TimeSpan DefaultMonitorSettleDelay = TimeSpan.FromSeconds(1.5);

    private readonly TimeSpan _monitorSettleDelay = monitorSettleDelay ?? DefaultMonitorSettleDelay;

    public bool SupportsSceneAdd => true;

    public bool SupportsEntityMonitor(EntityKind kind) => true;

    public bool SupportsOwnedImport => true;

    /// <summary>
    /// Imports a Cove-owned file into the <paramref name="scene"/> Eros movie without ever moving or deleting
    /// Cove's own file and without grabbing. <see cref="OwnedImportMode.InPlaceAdopt"/> (the folder-per-scene path)
    /// re-points the movie row's path to Cove's own scene folder and rescans, so Whisparr links the file already
    /// there — zero duplication; <see cref="OwnedImportMode.Copy"/> is the flat-layout fallback that copies the
    /// file into the movie's own folder. The file must sit where Whisparr can see it (shared storage).
    /// </summary>
    /// <remarks>
    /// Success is confirmed by re-reading the movie's <c>hasFile</c> (bounded); a queued-but-unlinked outcome is
    /// Unreachable, never a false Ok. Targets a movie that already exists in Whisparr as a fileless row (the caller
    /// only offers fileless scenes) and reads it back by its StashDB id.
    /// </remarks>
    public Task<WhisparrResult<bool>> ImportOwnedSceneAsync(
        string baseUrl, string apiKey, WhisparrMovie scene, string whisparrFilePath, OwnedImportMode mode, CancellationToken ct)
        => mode == OwnedImportMode.Copy
            ? ImportOwnedSceneByCopyAsync(baseUrl, apiKey, scene, whisparrFilePath, ct)
            : AdoptOwnedSceneInPlaceAsync(baseUrl, apiKey, scene, whisparrFilePath, ct);

    private async Task<WhisparrResult<bool>> AdoptOwnedSceneInPlaceAsync(
        string baseUrl, string apiKey, WhisparrMovie scene, string whisparrFilePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(scene.StashId))
        {
            return WhisparrResult<bool>.Unreachable("v3 owned-import requires the movie's StashDB id for verification");
        }

        var folder = ParentFolder(whisparrFilePath);
        if (folder is null)
        {
            return WhisparrResult<bool>.Unreachable($"cannot derive folder from '{whisparrFilePath}'");
        }

        // Loop-safety: adopting an owned scene issues ONLY a movie PUT (re-point) + a rescan command — no add and
        // no MoviesSearch — so it can never start a grab/re-ingest loop. The flip body echoes the resource with the
        // monitored state preserved and its top-level path set to the owned file's folder (Eros requires a non-empty
        // path); Eros then links the file already sitting there.
        var body = BuildSceneFlipBody(
            scene.Id, scene.ForeignId, scene.StashId, scene.Title, scene.Monitored,
            scene.QualityProfileId, scene.RootFolderPath, scene.Tags, folder);
        var repoint = await client.UpdateMovieAsync(baseUrl, apiKey, scene.Id, body, ct);
        if (!repoint.IsOk)
        {
            return Propagate<WhisparrMovie, bool>(repoint);
        }

        var rescanBody = JsonSerializer.Serialize(new { name = "RescanMovie", movieIds = new[] { scene.Id } });
        var rescan = await client.SendCommandAsync(baseUrl, apiKey, rescanBody, ct);
        if (!rescan.IsOk)
        {
            return Propagate<bool, bool>(rescan);
        }

        return await VerifyHasFileAsync(baseUrl, apiKey, scene.StashId, scene.Id, ct);
    }

    // The flat-layout fallback: a targeted ManualImport with importMode "copy" into the movie's OWN folder. Reached
    // ONLY when the orchestration detects a shared-directory layout (where a path re-point would collide two movies
    // on one directory). importMode "copy" (never "move"/"auto") leaves Cove's original exactly where it is —
    // Whisparr gets its own copy (a hardlink on a shared volume with "Use Hard Links" on). Never grabs.
    private async Task<WhisparrResult<bool>> ImportOwnedSceneByCopyAsync(
        string baseUrl, string apiKey, WhisparrMovie scene, string whisparrFilePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(scene.StashId))
        {
            return WhisparrResult<bool>.Unreachable("v3 owned-import requires the movie's StashDB id for verification");
        }

        var folder = ParentFolder(whisparrFilePath);
        if (folder is null)
        {
            return WhisparrResult<bool>.Unreachable($"cannot derive folder from '{whisparrFilePath}'");
        }

        var normalizedPath = whisparrFilePath.Replace('\\', '/');
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

        // The quality/languages are the listed objects verbatim — a synthesized quality does not import; `movieId`
        // targets the Eros movie (the v3 analogue of the v2 episodeIds).
        var body = JsonSerializer.Serialize(new
        {
            name = "ManualImport",
            importMode = "copy",
            files = new[]
            {
                new
                {
                    path = row.Path,
                    movieId = scene.Id,
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

        return await VerifyHasFileAsync(baseUrl, apiKey, scene.StashId, scene.Id, ct);
    }

    // Confirm the import linked a file: the command completes async, so re-read the movie's hasFile (bounded by
    // MonitorVerifyMaxAttempts). A queued-but-unlinked outcome is Unreachable, never a false Ok.
    private async Task<WhisparrResult<bool>> VerifyHasFileAsync(
        string baseUrl, string apiKey, string stashId, int movieId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MonitorVerifyMaxAttempts; attempt++)
        {
            await Task.Delay(_monitorSettleDelay, ct);

            var reread = await client.GetMovieByStashIdAsync(baseUrl, apiKey, stashId, ct);
            if (!reread.IsOk)
            {
                return Propagate<WhisparrMovie[], bool>(reread);
            }

            if (Array.Find(reread.Value!, m => m.Id == movieId) is { HasFile: true })
            {
                return WhisparrResult<bool>.Ok(true);
            }
        }

        return WhisparrResult<bool>.Unreachable("import queued but movie not linked");
    }

    // The owned file's parent directory (separators unified to '/'); null when the path has no derivable folder.
    private static string? ParentFolder(string whisparrFilePath)
    {
        var normalized = whisparrFilePath.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash <= 0 ? null : normalized[..lastSlash];
    }

    public Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.GetStatusAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<RootFolder[]>> ListRootFoldersAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.ListRootFoldersAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<QualityProfile[]>> ListQualityProfilesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.ListQualityProfilesAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<WhisparrMovie[]>> ListMoviesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.ListMoviesAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<WhisparrHistoryPage>> ListHistoryAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct)
        => client.ListHistoryAsync(baseUrl, apiKey, page, pageSize, ct);

    public Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string webhookUrl, CancellationToken ct)
        => client.RegisterWebhookAsync(baseUrl, apiKey, BuildNotificationPayload(webhookUrl), ct);

    public async Task<WhisparrResult<EntityMonitorResult>> SetEntityMonitorAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, bool monitored, MonitorScope scope,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var flip = kind == EntityKind.Studio
            ? await SetStudioMonitorAsync(baseUrl, apiKey, stashId, monitored, rootFolderPath, qualityProfileId, tagIds, ct)
            : await SetPerformerMonitorAsync(baseUrl, apiKey, stashId, monitored, rootFolderPath, qualityProfileId, tagIds, ct);
        if (!flip.IsOk)
        {
            return Propagate<EntityFlip, EntityMonitorResult>(flip);
        }

        var outcome = flip.Value!.Result;

        // Cove OWNS the population moment: only after a successful ON flip, fire a TARGETED metadata refresh
        // scoped to this one entity and WAIT for it to land, so the scope reconcile below acts on a populated
        // movie set rather than racing Whisparr's own scheduled refresh. Monitor-OFF (monitored:false) and the
        // idempotent OFF no-op never populate. A population failure propagates: the flip is durable but the
        // caller must know the catalogue did not populate.
        if (monitored)
        {
            var populate = await PopulateCatalogueAsync(baseUrl, apiKey, kind, flip.Value.WhisparrId, ct);
            if (!populate.IsOk)
            {
                return Propagate<bool, EntityMonitorResult>(populate);
            }
        }

        // Scope cascade over the attributed scenes. A bulk MONITOR toggle only (PUT /movie/editor) — it never
        // searches, so loop-safety holds. A cascade failure propagates: the container flip is durable but the
        // caller must know the scenes did not follow.
        var cascade = await CascadeSceneMonitorAsync(baseUrl, apiKey, kind, stashId, monitored, scope, ct);
        return cascade.IsOk ? WhisparrResult<EntityMonitorResult>.Ok(outcome) : Propagate<bool, EntityMonitorResult>(cascade);
    }

    // The flip outcome plus the entity's resolved Whisparr integer id — threaded out of the studio/performer
    // flip so the population refresh targets that EXACT id without a re-read. WhisparrId is 0 only on paths that
    // never populate (an absent-entity OFF no-op), so a refresh can never be built from a bogus id.
    private sealed record EntityFlip(EntityMonitorResult Result, int WhisparrId);

    // Cove-owned catalogue population: fire a TARGETED metadata refresh for the SINGLE resolved entity id, then
    // poll the queued command until Whisparr reports it completed (bounded by the same settle budget the
    // create-path verify uses). Loop-safety (why this is safe): the id array is ALWAYS exactly one element — an
    // empty studioIds/performerIds array would tell Whisparr to refresh EVERY entity and hammer StashDB, so it
    // is never constructed here; and a refresh is a metadata-only command that carries no search intent, so it
    // can never grab. A failed refresh POST propagates so the caller learns the population did not run.
    private async Task<WhisparrResult<bool>> PopulateCatalogueAsync(
        string baseUrl, string apiKey, EntityKind kind, int entityWhisparrId, CancellationToken ct)
    {
        var body = kind == EntityKind.Studio
            ? JsonSerializer.Serialize(new { name = "RefreshStudios", studioIds = new[] { entityWhisparrId } })
            : JsonSerializer.Serialize(new { name = "RefreshPerformers", performerIds = new[] { entityWhisparrId } });

        var queued = await client.SendCommandForIdAsync(baseUrl, apiKey, body, ct);
        if (!queued.IsOk)
        {
            return Propagate<int, bool>(queued);
        }

        var commandId = queued.Value;
        for (var attempt = 1; attempt <= MonitorVerifyMaxAttempts; attempt++)
        {
            // Let the async refresh make progress before reading its status (TimeSpan.Zero in tests).
            await Task.Delay(_monitorSettleDelay, ct);

            var status = await client.GetCommandAsync(baseUrl, apiKey, commandId, ct);
            if (!status.IsOk)
            {
                return Propagate<WhisparrCommand, bool>(status);
            }

            if (string.Equals(status.Value!.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return WhisparrResult<bool>.Ok(true);
            }
        }

        // Best-effort: the refresh was accepted but did not report completed within the budget. Return Ok and
        // let the reconcile act on whatever populated — the container stays monitored, so a late-arriving scene
        // is a legitimate "new release" (it arrives monitored under the still-monitored container), which is the
        // acceptable residual of not waiting unboundedly.
        return WhisparrResult<bool>.Ok(true);
    }

    // Reconcile the entity's attributed scenes (movie rows) to the chosen scope via the bulk monitor toggle
    // (PUT /movie/editor), which never searches — loop-safety holds. Fires only when turning monitoring ON; OFF
    // is a no-op (like Whisparr's own unmonitor, turning the container off leaves the scene-level flags alone).
    //
    // The `monitored` flag is the RSS-grab pivot: Whisparr's MonitoredMovieSpecification rejects an UNMONITORED
    // movie on an RSS/scheduled grab, so it stays visible-but-not-grab-eligible. That is what separates the two
    // scopes:
    //  - AllScenes: mark every attributed row monitored:true (the deliberate acquire-it-all choice).
    //  - NewReleases: UNMONITOR the discovered fileless back-catalogue (attributed AND !HasFile — the un-owned,
    //    grab-eligible set) so it is not RSS-grabbed, while the CONTAINER studio/performer stays monitored so
    //    genuinely-new future scenes still arrive monitored. Owned rows (HasFile) are left untouched.
    // The population refresh has already run by the time this executes, so the movie set reflects the freshly
    // fetched catalogue rather than racing it.
    private async Task<WhisparrResult<bool>> CascadeSceneMonitorAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, bool monitored, MonitorScope scope, CancellationToken ct)
    {
        if (!monitored)
        {
            return WhisparrResult<bool>.Ok(true);
        }

        if (scope == MonitorScope.AllScenes)
        {
            var allResult = await CollectAttributedEntityIdsAsync(baseUrl, apiKey, kind, stashId, monitoredOnly: false, filelessOnly: false, ct);
            if (!allResult.IsOk)
            {
                return Propagate<int[], bool>(allResult);
            }

            var allIds = allResult.Value!;
            if (allIds.Length == 0)
            {
                return WhisparrResult<bool>.Ok(true);
            }

            var monitorBody = JsonSerializer.Serialize(new { movieIds = allIds, monitored = true });
            return await client.BulkMonitorMoviesAsync(baseUrl, apiKey, monitorBody, ct);
        }

        // NewReleases: collect only the fileless (un-owned) attributed rows — the same attribution predicate the
        // status count uses, so the reconcile can never diverge from what the UI reports — and unmonitor them.
        var backCatalogue = await CollectAttributedEntityIdsAsync(baseUrl, apiKey, kind, stashId, monitoredOnly: false, filelessOnly: true, ct);
        if (!backCatalogue.IsOk)
        {
            return Propagate<int[], bool>(backCatalogue);
        }

        var backIds = backCatalogue.Value!;
        if (backIds.Length == 0)
        {
            return WhisparrResult<bool>.Ok(true);
        }

        var unmonitorBody = JsonSerializer.Serialize(new { movieIds = backIds, monitored = false });
        return await client.BulkMonitorMoviesAsync(baseUrl, apiKey, unmonitorBody, ct);
    }

    public Task<WhisparrResult<EntityStatus>> GetEntityStatusAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, CancellationToken ct)
        => kind == EntityKind.Studio
            ? GetStudioStatusAsync(baseUrl, apiKey, stashId, ct)
            : GetPerformerStatusAsync(baseUrl, apiKey, stashId, ct);

    public Task<WhisparrResult<WhisparrExclusion[]>> ListExclusionsAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.GetExclusionsAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<WhisparrRelease[]>> GetReleasesAsync(string baseUrl, string apiKey, int movieId, CancellationToken ct)
        => client.GetReleasesAsync(baseUrl, apiKey, movieId, ct);

    // Add-exclusion: POST the exclusion body; a duplicate (409 or the "exists" 400 body) is the same
    // idempotent success as a fresh add (re-excluding never creates a second row). Issues no search.
    public async Task<WhisparrResult<bool>> AddExclusionAsync(
        string baseUrl, string apiKey, string stashId, string? title, int? year, CancellationToken ct)
    {
        var result = await client.CreateExclusionAsync(
            baseUrl, apiKey, BuildExclusionAddBody(stashId, title, year), ct);
        return result.IsOk || result.State == WhisparrResultState.Conflict
            ? WhisparrResult<bool>.Ok(true)
            : Propagate<WhisparrExclusion, bool>(result);
    }

    // Remove-exclusion: resolve the DELETE target id SERVER-SIDE by matching the scene's StashDB id
    // against the exclusion list's foreignId (never a caller-supplied id), then DELETE by that id.
    // A scene with no matching exclusion is an idempotent Ok no-op (nothing to remove). Issues no search.
    public async Task<WhisparrResult<bool>> RemoveExclusionAsync(
        string baseUrl, string apiKey, string stashId, CancellationToken ct)
    {
        var listResult = await client.GetExclusionsAsync(baseUrl, apiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrExclusion[], bool>(listResult);
        }

        var match = Array.Find(
            listResult.Value!,
            e => string.Equals(e.ForeignId, stashId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return WhisparrResult<bool>.Ok(true);
        }

        var deleteResult = await client.DeleteExclusionAsync(baseUrl, apiKey, match.Id, ct);
        return deleteResult.IsOk ? WhisparrResult<bool>.Ok(true) : Propagate<bool, bool>(deleteResult);
    }

    // Interactive grab: POST the guid+indexerId+movieId to grab THIS release. A distinct single-shot verb.
    public async Task<WhisparrResult<bool>> GrabReleaseAsync(
        string baseUrl, string apiKey, string guid, int indexerId, int movieId, CancellationToken ct)
    {
        var result = await client.GrabReleaseAsync(baseUrl, apiKey, BuildGrabReleaseBody(guid, indexerId, movieId), ct);
        return result.IsOk ? WhisparrResult<bool>.Ok(true) : Propagate<bool, bool>(result);
    }

    // Upgrade search: reuse the MoviesSearch command (Whisparr grabs an upgrade only when the
    // movie is monitored and the cutoff is unmet — eligibility is enforced server-side). A distinct grab verb;
    // an empty id set issues NO command (Ok no-op), mirroring SearchScenesAsync.
    public async Task<WhisparrResult<BulkActionResult>> SearchForUpgradesAsync(
        string baseUrl, string apiKey, IReadOnlyList<int> movieIds, CancellationToken ct)
    {
        if (movieIds.Count == 0)
        {
            return WhisparrResult<BulkActionResult>.Ok(BulkActionResult.Empty);
        }

        var body = JsonSerializer.Serialize(new { name = "MoviesSearch", movieIds });
        var result = await client.SendCommandAsync(baseUrl, apiKey, body, ct);
        return result.IsOk
            ? WhisparrResult<BulkActionResult>.Ok(new BulkActionResult(movieIds.Count, movieIds.Count, Failed: 0))
            : Propagate<bool, BulkActionResult>(result);
    }

    // Add: POST the movie body (searchForMovie defaults false — register, don't grab), a 2xx
    // is the created row (Added:true), a 409 re-reads to the existing row (Added:false, never a duplicate).
    public async Task<WhisparrResult<SceneActionResult>> AddSceneAsync(
        string baseUrl, string apiKey, string stashId, string? title, bool monitored, bool searchForMovie,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var createResult = await client.CreateMovieAsync(
            baseUrl, apiKey,
            BuildSceneAddBody(stashId, title, monitored, searchForMovie, rootFolderPath, qualityProfileId, tagIds), ct);

        if (createResult.IsOk)
        {
            var movie = createResult.Value!;
            return WhisparrResult<SceneActionResult>.Ok(new SceneActionResult(movie.Id, Added: true, movie.Monitored, movie.Path));
        }

        if (createResult.State == WhisparrResultState.Conflict)
        {
            // 409/exists = success: re-read to resolve the existing row, report it as not added.
            var reread = await client.GetMovieByStashIdAsync(baseUrl, apiKey, stashId, ct);
            if (!reread.IsOk)
            {
                return Propagate<WhisparrMovie[], SceneActionResult>(reread);
            }

            var existing = reread.Value!.FirstOrDefault();
            return existing is null
                ? WhisparrResult<SceneActionResult>.Unreachable("movie create conflicted but no row on re-read")
                : WhisparrResult<SceneActionResult>.Ok(new SceneActionResult(existing.Id, Added: false, existing.Monitored, existing.Path));
        }

        return Propagate<WhisparrMovie, SceneActionResult>(createResult);
    }

    // Add-then-flip for a scene. Absent + ON: add monitored:false (searchForMovie:false) then PUT true.
    // Present: PUT the requested state. Absent + OFF: no-op (nothing to unmonitor). NEVER searches.
    public async Task<WhisparrResult<SceneActionResult>> SetSceneMonitorAsync(
        string baseUrl, string apiKey, string stashId, string? title, bool monitored,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var getResult = await client.GetMovieByStashIdAsync(baseUrl, apiKey, stashId, ct);
        if (!getResult.IsOk)
        {
            return Propagate<WhisparrMovie[], SceneActionResult>(getResult);
        }

        var existing = getResult.Value!.FirstOrDefault();

        if (existing is null)
        {
            if (!monitored)
            {
                // Absent + OFF: nothing to unmonitor, nothing to add.
                return WhisparrResult<SceneActionResult>.Ok(new SceneActionResult(MovieId: 0, Added: false, Monitored: false));
            }

            // Absent + ON: register monitored:false (never grabs), then flip to monitored:true.
            var addResult = await AddSceneAsync(
                baseUrl, apiKey, stashId, title, monitored: false, searchForMovie: false,
                rootFolderPath, qualityProfileId, tagIds, ct);
            if (!addResult.IsOk)
            {
                return addResult;
            }

            var addValue = addResult.Value!;
            var flipBody = BuildSceneFlipBody(
                addValue.MovieId, stashId, stashId, title, monitored: true, qualityProfileId, rootFolderPath, tagIds,
                addValue.Path);
            var flip = await client.UpdateMovieAsync(baseUrl, apiKey, addValue.MovieId, flipBody, ct);
            return flip.IsOk
                ? WhisparrResult<SceneActionResult>.Ok(new SceneActionResult(addValue.MovieId, addValue.Added, Monitored: true))
                : Propagate<WhisparrMovie, SceneActionResult>(flip);
        }

        if (!monitored && !existing.Monitored)
        {
            // Present + already-unmonitored OFF: idempotent no-op, no redundant PUT.
            return WhisparrResult<SceneActionResult>.Ok(new SceneActionResult(existing.Id, Added: false, Monitored: false));
        }

        var putBody = BuildSceneFlipBody(
            existing.Id, existing.ForeignId, existing.StashId, existing.Title, monitored,
            existing.QualityProfileId, existing.RootFolderPath, existing.Tags, existing.Path);
        var putResult = await client.UpdateMovieAsync(baseUrl, apiKey, existing.Id, putBody, ct);
        return putResult.IsOk
            ? WhisparrResult<SceneActionResult>.Ok(new SceneActionResult(existing.Id, Added: false, Monitored: monitored))
            : Propagate<WhisparrMovie, SceneActionResult>(putResult);
    }

    // Search: the ONLY grab-capable verb. An empty id set issues NO command (Ok no-op).
    public async Task<WhisparrResult<BulkActionResult>> SearchScenesAsync(
        string baseUrl, string apiKey, IReadOnlyList<int> movieIds, CancellationToken ct)
    {
        if (movieIds.Count == 0)
        {
            return WhisparrResult<BulkActionResult>.Ok(BulkActionResult.Empty);
        }

        var body = JsonSerializer.Serialize(new { name = "MoviesSearch", movieIds });
        var result = await client.SendCommandAsync(baseUrl, apiKey, body, ct);
        return result.IsOk
            ? WhisparrResult<BulkActionResult>.Ok(new BulkActionResult(movieIds.Count, movieIds.Count, Failed: 0))
            : Propagate<bool, BulkActionResult>(result);
    }

    public async Task<WhisparrResult<WhisparrFileSettings>> GetFileSettingsAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var naming = await client.GetNamingConfigAsync(baseUrl, apiKey, ct);
        if (!naming.IsOk)
        {
            return Propagate<NamingConfig, WhisparrFileSettings>(naming);
        }

        var media = await client.GetMediaManagementConfigAsync(baseUrl, apiKey, ct);
        if (!media.IsOk)
        {
            return Propagate<MediaManagementConfig, WhisparrFileSettings>(media);
        }

        return WhisparrResult<WhisparrFileSettings>.Ok(SettingsOf(naming.Value!, media.Value!));
    }

    public async Task<WhisparrResult<WhisparrFileSettings>> EditFileSettingsAsync(
        string baseUrl, string apiKey, WhisparrFileSettingsRequest request, CancellationToken ct)
    {
        // A config singleton is a whole-object replace: a partial PUT wipes every field it omits. So each write
        // is read-modify-write — GET the full object, flip ONLY the requested booleans (the DTO carries every
        // other field verbatim in its raw-JsonElement bucket, so the round-trip re-emits them byte-for-value),
        // then PUT the complete object back. A non-Ok GET short-circuits before any PUT.
        var naming = await client.GetNamingConfigAsync(baseUrl, apiKey, ct);
        if (!naming.IsOk)
        {
            return Propagate<NamingConfig, WhisparrFileSettings>(naming);
        }

        if (request.RenameMovies is not null || request.ReplaceIllegalCharacters is not null)
        {
            var flipped = naming.Value! with
            {
                RenameMovies = request.RenameMovies ?? naming.Value.RenameMovies,
                ReplaceIllegalCharacters = request.ReplaceIllegalCharacters ?? naming.Value.ReplaceIllegalCharacters,
            };
            var put = await client.UpdateNamingConfigAsync(
                baseUrl, apiKey, JsonSerializer.Serialize(flipped, WhisparrJsonContext.Default.NamingConfig), ct);
            if (!put.IsOk)
            {
                return Propagate<NamingConfig, WhisparrFileSettings>(put);
            }

            naming = put;
        }

        var media = await client.GetMediaManagementConfigAsync(baseUrl, apiKey, ct);
        if (!media.IsOk)
        {
            return Propagate<MediaManagementConfig, WhisparrFileSettings>(media);
        }

        if (request.AutoRenameFolders is not null || request.DeleteEmptyFolders is not null)
        {
            var flipped = media.Value! with
            {
                AutoRenameFolders = request.AutoRenameFolders ?? media.Value.AutoRenameFolders,
                DeleteEmptyFolders = request.DeleteEmptyFolders ?? media.Value.DeleteEmptyFolders,
            };
            var put = await client.UpdateMediaManagementConfigAsync(
                baseUrl, apiKey, JsonSerializer.Serialize(flipped, WhisparrJsonContext.Default.MediaManagementConfig), ct);
            if (!put.IsOk)
            {
                return Propagate<MediaManagementConfig, WhisparrFileSettings>(put);
            }

            media = put;
        }

        return WhisparrResult<WhisparrFileSettings>.Ok(SettingsOf(naming.Value!, media.Value!));
    }

    private static WhisparrFileSettings SettingsOf(NamingConfig naming, MediaManagementConfig media)
        => new(naming.RenameMovies, naming.ReplaceIllegalCharacters, media.AutoRenameFolders, media.DeleteEmptyFolders);

    // Attribution: resolve the entity, then filter the existing movie set by the SAME predicate the
    // status uses (studio by title, performer by foreign id), optionally to monitored. No StashDB call.
    public Task<WhisparrResult<int[]>> ListAttributedMovieIdsAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, bool monitoredOnly, CancellationToken ct)
        => CollectAttributedEntityIdsAsync(baseUrl, apiKey, kind, stashId, monitoredOnly, filelessOnly: false, ct);

    // The entity-resolve + attributed-id projection shared by the public search-id surface and the NewReleases
    // back-catalogue reconcile. `filelessOnly` narrows to the discovered, un-owned rows (attributed AND
    // !HasFile) — the grab-eligible back-catalogue — so the NewReleases unmonitor can never diverge from the
    // attribution the status count uses.
    private async Task<WhisparrResult<int[]>> CollectAttributedEntityIdsAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, bool monitoredOnly, bool filelessOnly, CancellationToken ct)
    {
        if (kind == EntityKind.Studio)
        {
            var studioResult = await client.GetStudioByStashIdAsync(baseUrl, apiKey, stashId, ct);
            if (!studioResult.IsOk)
            {
                return Propagate<WhisparrStudio[], int[]>(studioResult);
            }

            var studio = studioResult.Value!.FirstOrDefault();
            return studio is null
                ? WhisparrResult<int[]>.Ok([])
                : await CollectAttributedIdsAsync(baseUrl, apiKey, StudioAttribution(studio), monitoredOnly, filelessOnly, ct);
        }

        var performerResult = await client.GetPerformerByStashIdAsync(baseUrl, apiKey, stashId, ct);
        if (performerResult.State == WhisparrResultState.Absent)
        {
            return WhisparrResult<int[]>.Ok([]);
        }

        if (!performerResult.IsOk)
        {
            return Propagate<WhisparrPerformer, int[]>(performerResult);
        }

        return await CollectAttributedIdsAsync(baseUrl, apiKey, PerformerAttribution(stashId), monitoredOnly, filelessOnly, ct);
    }

    // The attributed-id collector: read the movie set and project the ids matching the predicate (optionally
    // monitored, optionally fileless-only). Unlike the status count (which degrades a failed movie read to
    // 0-of-0), a bulk toggle MUST act on the real set, so a non-Ok movie list propagates rather than silently
    // acting on a partial set.
    // Scale follow-up (tracked): this reads the FULL movie set (GET /api/v3/movie) and filters in-memory. A
    // scoped per-entity movie query would be cheaper on a large library, but no scoped endpoint yields HasFile
    // today, so the full-read attribution stays — to be revisited against a live instance.
    private async Task<WhisparrResult<int[]>> CollectAttributedIdsAsync(
        string baseUrl, string apiKey, Func<WhisparrMovie, bool> attributed, bool monitoredOnly, bool filelessOnly, CancellationToken ct)
    {
        var moviesResult = await client.ListMoviesAsync(baseUrl, apiKey, ct);
        if (!moviesResult.IsOk)
        {
            return Propagate<WhisparrMovie[], int[]>(moviesResult);
        }

        var ids = moviesResult.Value!
            .Where(attributed)
            .Where(m => !monitoredOnly || m.Monitored)
            .Where(m => !filelessOnly || !m.HasFile)
            .Select(m => m.Id)
            .ToArray();
        return WhisparrResult<int[]>.Ok(ids);
    }

    // Studio add-then-flip. The `?stashId=` query answers an ABSENT studio with an empty array
    // (Ok, never an error), so absence is "no matching row" here — not a 404. On ON+absent: POST monitored:false
    // (a 409/exists re-reads to the existing row, added:false, never a duplicate), then PUT the requested state.
    // On OFF: PUT monitored:false only when present; an absent studio is already unmonitored (no add, no delete).
    private async Task<WhisparrResult<EntityFlip>> SetStudioMonitorAsync(
        string baseUrl, string apiKey, string stashId, bool monitored,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var getResult = await client.GetStudioByStashIdAsync(baseUrl, apiKey, stashId, ct);
        if (!getResult.IsOk)
        {
            return Propagate<WhisparrStudio[], EntityFlip>(getResult);
        }

        var existing = getResult.Value!.FirstOrDefault();
        var added = false;

        if (existing is null)
        {
            if (!monitored)
            {
                // OFF on an absent studio: nothing to unmonitor, nothing to add/delete.
                return WhisparrResult<EntityFlip>.Ok(new EntityFlip(new EntityMonitorResult(Added: false, Monitored: false), WhisparrId: 0));
            }

            var createResult = await client.CreateStudioAsync(
                baseUrl, apiKey,
                BuildStudioAddBody(stashId, rootFolderPath, qualityProfileId, tagIds), ct);

            if (createResult.State == WhisparrResultState.Conflict)
            {
                // 409/exists = success: re-read to obtain the id, then proceed to the PUT. Not "added".
                var reread = await client.GetStudioByStashIdAsync(baseUrl, apiKey, stashId, ct);
                if (!reread.IsOk)
                {
                    return Propagate<WhisparrStudio[], EntityFlip>(reread);
                }

                existing = reread.Value!.FirstOrDefault();
                if (existing is null)
                {
                    return WhisparrResult<EntityFlip>.Unreachable("studio create conflicted but no row on re-read");
                }
            }
            else if (createResult.IsOk)
            {
                existing = createResult.Value!;
                added = true;
            }
            else
            {
                return Propagate<WhisparrStudio, EntityFlip>(createResult);
            }
        }
        else if (!monitored && !existing.Monitored)
        {
            // OFF on an already-unmonitored studio: idempotent no-op, no redundant PUT.
            return WhisparrResult<EntityFlip>.Ok(new EntityFlip(new EntityMonitorResult(added, Monitored: false), existing.Id));
        }

        var putResult = await client.UpdateStudioAsync(
            baseUrl, apiKey, existing.Id, BuildStudioFlipBody(existing, monitored), ct);
        if (!putResult.IsOk)
        {
            return Propagate<WhisparrStudio, EntityFlip>(putResult);
        }

        // Fix: a fresh create triggers Whisparr's post-create RefreshStudios, which rebuilds the row
        // AFTER this flip PUT and can reset `monitored`. Re-read after a short settle and re-assert until a
        // GET confirms the requested state (bounded), and return the VERIFIED read-back — never the optimistic
        // requested value. A 409/already-exists add (added:false) triggers no refresh, so its requested state
        // is already durable truth and needs no verify pass.
        if (added)
        {
            var verified = await VerifyStudioMonitoredAsync(baseUrl, apiKey, stashId, monitored, ct);
            return verified.IsOk
                ? WhisparrResult<EntityFlip>.Ok(new EntityFlip(new EntityMonitorResult(added, verified.Value), existing.Id))
                : Propagate<bool, EntityFlip>(verified);
        }

        return WhisparrResult<EntityFlip>.Ok(new EntityFlip(new EntityMonitorResult(added, monitored), existing.Id));
    }

    // Create-path monitor verify: after the flip PUT on a freshly-created studio, Whisparr's async
    // RefreshStudios can reset `monitored` (the PUT's own 200 is NOT authoritative — the refresh runs after it
    // returns). Settle, re-read, and — if the state reverted — re-assert it (idempotent PUT, still no search),
    // repeating up to the attempt budget until a read-back confirms the requested state. Returns the last
    // VERIFIED read (honest even if it never stuck), so `/monitor` and the UI reflect durable truth.
    private async Task<WhisparrResult<bool>> VerifyStudioMonitoredAsync(
        string baseUrl, string apiKey, string stashId, bool desired, CancellationToken ct)
    {
        var observed = desired;
        for (var attempt = 1; attempt <= MonitorVerifyMaxAttempts; attempt++)
        {
            // Let an in-flight post-create RefreshStudios land before trusting the read (TimeSpan.Zero in tests).
            await Task.Delay(_monitorSettleDelay, ct);

            var reread = await client.GetStudioByStashIdAsync(baseUrl, apiKey, stashId, ct);
            if (!reread.IsOk)
            {
                return Propagate<WhisparrStudio[], bool>(reread);
            }

            var row = reread.Value!.FirstOrDefault();
            if (row is null)
            {
                return WhisparrResult<bool>.Unreachable("studio vanished during monitor verify");
            }

            observed = row.Monitored;
            if (observed == desired)
            {
                // Confirmed durable — no unnecessary extra PUT on a studio that already stuck.
                return WhisparrResult<bool>.Ok(observed);
            }

            // Reverted by the refresh — re-assert the requested state, then loop to re-verify. Skip on the
            // final attempt: a re-PUT with no subsequent read-back would be unverifiable, so the last pass is
            // read-only and we report what Whisparr actually holds.
            if (attempt < MonitorVerifyMaxAttempts)
            {
                var reput = await client.UpdateStudioAsync(
                    baseUrl, apiKey, row.Id, BuildStudioFlipBody(row, desired), ct);
                if (!reput.IsOk)
                {
                    return Propagate<WhisparrStudio, bool>(reput);
                }
            }
        }

        return WhisparrResult<bool>.Ok(observed);
    }

    // Performer add-then-flip. The performer GET answers an ABSENT performer with HTTP 404 OR
    // 500 (the client classifies BOTH as WhisparrResultState.Absent), so absence is a state here — not an
    // empty array. Same add-then-flip / 409-as-success / OFF-only-PUT discipline as the studio path.
    private async Task<WhisparrResult<EntityFlip>> SetPerformerMonitorAsync(
        string baseUrl, string apiKey, string stashId, bool monitored,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var getResult = await client.GetPerformerByStashIdAsync(baseUrl, apiKey, stashId, ct);
        if (getResult.State is not (WhisparrResultState.Ok or WhisparrResultState.Absent))
        {
            return Propagate<WhisparrPerformer, EntityFlip>(getResult);
        }

        var existing = getResult.State == WhisparrResultState.Ok ? getResult.Value : null;
        var added = false;

        if (existing is null)
        {
            if (!monitored)
            {
                return WhisparrResult<EntityFlip>.Ok(new EntityFlip(new EntityMonitorResult(Added: false, Monitored: false), WhisparrId: 0));
            }

            var createResult = await client.CreatePerformerAsync(
                baseUrl, apiKey,
                BuildPerformerAddBody(stashId, rootFolderPath, qualityProfileId, tagIds), ct);

            if (createResult.State == WhisparrResultState.Conflict)
            {
                var reread = await client.GetPerformerByStashIdAsync(baseUrl, apiKey, stashId, ct);
                if (!reread.IsOk)
                {
                    return Propagate<WhisparrPerformer, EntityFlip>(reread);
                }

                existing = reread.Value!;
            }
            else if (createResult.IsOk)
            {
                existing = createResult.Value!;
                added = true;
            }
            else
            {
                return Propagate<WhisparrPerformer, EntityFlip>(createResult);
            }
        }
        else if (!monitored && !existing.Monitored)
        {
            return WhisparrResult<EntityFlip>.Ok(new EntityFlip(new EntityMonitorResult(added, Monitored: false), existing.Id));
        }

        var putResult = await client.UpdatePerformerAsync(
            baseUrl, apiKey, existing.Id, BuildPerformerFlipBody(existing, monitored), ct);
        if (!putResult.IsOk)
        {
            return Propagate<WhisparrPerformer, EntityFlip>(putResult);
        }

        return WhisparrResult<EntityFlip>.Ok(new EntityFlip(new EntityMonitorResult(added, monitored), existing.Id));
    }

    // Studio status: monitored flag + Whisparr's own scenesPresent/scenesTotal, read off the studio
    // resource (no movie-set scan). An absent studio is added:false / 0-of-0.
    private async Task<WhisparrResult<EntityStatus>> GetStudioStatusAsync(
        string baseUrl, string apiKey, string stashId, CancellationToken ct)
    {
        var getResult = await client.GetStudioByStashIdAsync(baseUrl, apiKey, stashId, ct);
        if (!getResult.IsOk)
        {
            return Propagate<WhisparrStudio[], EntityStatus>(getResult);
        }

        var studio = getResult.Value!.FirstOrDefault();
        return WhisparrResult<EntityStatus>.Ok(studio is null
            ? new EntityStatus(Added: false, Monitored: false, ScenesPresent: 0, ScenesTotal: 0)
            : new EntityStatus(Added: true, studio.Monitored, studio.SceneCount, studio.TotalSceneCount));
    }

    // Performer status: monitored flag + Whisparr's own scenesPresent/scenesTotal off the performer
    // resource. An absent performer answers HTTP 404/500 (classified Absent) -> added:false / 0-of-0.
    private async Task<WhisparrResult<EntityStatus>> GetPerformerStatusAsync(
        string baseUrl, string apiKey, string stashId, CancellationToken ct)
    {
        var getResult = await client.GetPerformerByStashIdAsync(baseUrl, apiKey, stashId, ct);
        if (getResult.State == WhisparrResultState.Absent)
        {
            return WhisparrResult<EntityStatus>.Ok(new EntityStatus(Added: false, Monitored: false, ScenesPresent: 0, ScenesTotal: 0));
        }

        if (!getResult.IsOk)
        {
            return Propagate<WhisparrPerformer, EntityStatus>(getResult);
        }

        var performer = getResult.Value!;
        return WhisparrResult<EntityStatus>.Ok(new EntityStatus(Added: true, performer.Monitored, performer.SceneCount, performer.TotalSceneCount));
    }

    /// <summary>
    /// Status for MANY entities from the PRE-FETCHED entity list, so paging a large library re-uses the cached
    /// list and this stays pure in-memory work. Each requested StashDB id is matched to its Whisparr entity by
    /// <c>ForeignId</c> (absent → added:false); the count is Whisparr's own scenesPresent/scenesTotal off the
    /// matched resource — no movie set needed.
    /// </summary>
    public static IReadOnlyDictionary<string, EntityStatus> ClassifyEntityStatusBatch(
        EntityKind kind, IReadOnlyList<string> stashIds,
        WhisparrStudio[] studios, WhisparrPerformer[] performers)
    {
        var result = new Dictionary<string, EntityStatus>(StringComparer.OrdinalIgnoreCase);
        if (kind == EntityKind.Studio)
        {
            var byForeignId = IndexByForeignId(studios, s => s.ForeignId);
            foreach (var stashId in stashIds)
            {
                result[stashId] = byForeignId.TryGetValue(stashId, out var studio)
                    ? new EntityStatus(Added: true, studio.Monitored, studio.SceneCount, studio.TotalSceneCount)
                    : new EntityStatus(Added: false, Monitored: false, ScenesPresent: 0, ScenesTotal: 0);
            }
        }
        else
        {
            var byForeignId = IndexByForeignId(performers, p => p.ForeignId);
            foreach (var stashId in stashIds)
            {
                result[stashId] = byForeignId.TryGetValue(stashId, out var performer)
                    ? new EntityStatus(Added: true, performer.Monitored, performer.SceneCount, performer.TotalSceneCount)
                    : new EntityStatus(Added: false, Monitored: false, ScenesPresent: 0, ScenesTotal: 0);
            }
        }

        return result;
    }

    // First-wins index by ForeignId (the StashDB id), skipping rows with none — the batch match key.
    private static Dictionary<string, T> IndexByForeignId<T>(IReadOnlyList<T> rows, Func<T, string?> foreignId)
    {
        var index = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (foreignId(row) is { Length: > 0 } id && !index.ContainsKey(id))
            {
                index[id] = row;
            }
        }

        return index;
    }

    // The single studio attribution predicate: a movie belongs to the studio when its
    // studioTitle equals the studio's title (the live v3 movie row carries NO studio foreign id). A
    // studio with no title attributes nothing. Shared by GetStudioStatusAsync and ListAttributedMovieIdsAsync
    // so preview counts and the search-all id set can never diverge.
    private static Func<WhisparrMovie, bool> StudioAttribution(WhisparrStudio studio)
        => movie => studio.Title is { Length: > 0 } title
            && string.Equals(movie.StudioTitle, title, StringComparison.OrdinalIgnoreCase);

    // The single performer attribution predicate: a movie belongs to the performer when its
    // performerForeignIds contains the StashDB id. Shared by GetPerformerStatusAsync and
    // ListAttributedMovieIdsAsync.
    private static Func<WhisparrMovie, bool> PerformerAttribution(string stashId)
        => movie => movie.PerformerForeignIds is not null
            && movie.PerformerForeignIds.Contains(stashId, StringComparer.OrdinalIgnoreCase);

    // The v3 movie ADD body: the StashDB id in BOTH foreignId and stashId (per the verified
    // v3 API), the caller's monitored flag, the root/profile, the origin tag id(s), and
    // addOptions.searchForMovie. CRITICAL (loop-safety): searchForMovie is whatever the caller passed
    // and the interface DEFAULTS it false — an add registers without grabbing; only SearchScenesAsync grabs.
    private static string BuildSceneAddBody(
        string stashId, string? title, bool monitored, bool searchForMovie,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds)
        => JsonSerializer.Serialize(new
        {
            foreignId = stashId,
            stashId,
            // Whisparr Eros rejects an add whose title is empty ("'Title' must not be
            // empty."). Callers resolve a Cove-derived title, but a last-resort non-empty fallback here means
            // no add path can ever emit a null/empty title.
            title = NonEmptyTitle(title, stashId),
            monitored,
            qualityProfileId,
            rootFolderPath,
            addOptions = new { searchForMovie },
            tags = tagIds,
        });

    // The v3 movie FLIP body: echo the movie resource with monitored set to the target state,
    // preserving foreignId/stashId/root/tags so the toggle never clears the origin tag or re-routes the movie.
    // CRITICAL: no addOptions/searchForMovie — a flip never searches. Whisparr Eros's
    // PUT /movie/{id} REQUIRES a non-empty top-level `path` (the movie's on-disk directory) — omit it and the
    // flip is rejected 400 "'Path' must not be empty." — so the caller carries the created/existing movie's path.
    private static string BuildSceneFlipBody(
        int id, string? foreignId, string? stashId, string? title, bool monitored,
        int? qualityProfileId, string? rootFolderPath, IEnumerable<int>? tags, string? path)
        => JsonSerializer.Serialize(new
        {
            id,
            foreignId,
            stashId,
            title = NonEmptyTitle(title, stashId ?? foreignId),
            monitored,
            qualityProfileId,
            rootFolderPath,
            path,
            tags = tags ?? [],
        });

    // A never-null/empty movie title (Bug D): the caller's title if present, else a stable "Scene {id}" from
    // the StashDB id so Whisparr Eros's non-empty-title validation always passes.
    private static string NonEmptyTitle(string? title, string? stashId)
        => string.IsNullOrWhiteSpace(title) ? $"Scene {stashId}" : title;

    // The v3 exclusion ADD body: the StashDB id as foreignId (the field the exclusion list matches on
    // remove) plus a non-empty display title + year. Eros's POST /api/v3/exclusions binds the title/year
    // as `movieTitle`/`movieYear` — a plain `title` fails its "'Movie Title' must not be empty" validation
    // with a 400 — so the field names must be exactly these. No search/command — an exclusion never grabs.
    private static string BuildExclusionAddBody(string stashId, string? title, int? year)
        => JsonSerializer.Serialize(new
        {
            foreignId = stashId,
            movieTitle = NonEmptyTitle(title, stashId),
            movieYear = year,
        });

    // The v3 interactive-grab body: the guid+indexerId pair Whisparr needs to address ONE release (a
    // guid alone is ambiguous across indexers).
    // movieId maps the release to its movie: the interactive-search release row carries movieId:null, so
    // Whisparr needs it echoed here or it answers 404 "Unable to find matching movie".
    private static string BuildGrabReleaseBody(string guid, int indexerId, int movieId)
        => JsonSerializer.Serialize(new { guid, indexerId, movieId });

    // The v3 studio ADD body: monitored ALWAYS false here (a separate PUT sets the target
    // state), the rootFolderPath + qualityProfileId from the stored connection, and the origin tag id(s)
    // for attribution. CRITICAL (loop-safety): NO addOptions/searchForMovie — monitor-add never searches.
    private static string BuildStudioAddBody(string stashId, string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds)
        => JsonSerializer.Serialize(new
        {
            foreignId = stashId,
            monitored = false,
            qualityProfileId,
            rootFolderPath,
            tags = tagIds,
        });

    // The v3 studio FLIP body: echo the existing resource with monitored set to the target state.
    private static string BuildStudioFlipBody(WhisparrStudio studio, bool monitored)
        => JsonSerializer.Serialize(new
        {
            id = studio.Id,
            foreignId = studio.ForeignId,
            title = studio.Title,
            monitored,
            qualityProfileId = studio.QualityProfileId,
            rootFolderPath = studio.RootFolderPath,
            tags = studio.Tags ?? [],
        });

    // The v3 performer ADD body: mirrors the studio add — monitored:false, root/profile, origin
    // tags, and no search.
    private static string BuildPerformerAddBody(string stashId, string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds)
        => JsonSerializer.Serialize(new
        {
            foreignId = stashId,
            monitored = false,
            qualityProfileId,
            rootFolderPath,
            tags = tagIds,
        });

    // The v3 performer FLIP body: echo the existing resource with monitored set to the target state.
    private static string BuildPerformerFlipBody(WhisparrPerformer performer, bool monitored)
        => JsonSerializer.Serialize(new
        {
            id = performer.Id,
            foreignId = performer.ForeignId,
            fullName = performer.FullName,
            monitored,
            qualityProfileId = performer.QualityProfileId,
            rootFolderPath = performer.RootFolderPath,
            tags = performer.Tags ?? [],
        });

    // Re-shape a non-Ok result of one payload type into the same state for the monitor/status return type — a
    // failed read/write propagates verbatim (state + diagnostic) rather than inventing a success. An unexpected
    // Absent/Conflict reaching here (they are handled inline on the paths that declare them) collapses to a
    // classified Unreachable rather than a silent Ok.
    private static WhisparrResult<TTo> Propagate<TFrom, TTo>(WhisparrResult<TFrom> source)
        => source.State switch
        {
            WhisparrResultState.BadKey => WhisparrResult<TTo>.BadKey(),
            WhisparrResultState.NotWhisparr => WhisparrResult<TTo>.NotWhisparr(),
            WhisparrResultState.VersionMismatch => WhisparrResult<TTo>.VersionMismatch(source.DetectedVersion ?? string.Empty),
            WhisparrResultState.Rejected => WhisparrResult<TTo>.Rejected(source.Reason ?? "rejected"),
            WhisparrResultState.Absent => WhisparrResult<TTo>.Unreachable("unexpected absent"),
            WhisparrResultState.Conflict => WhisparrResult<TTo>.Unreachable("unexpected conflict"),
            _ => WhisparrResult<TTo>.Unreachable(source.Reason ?? "unreachable"),
        };

    // The v3 Webhook connection payload. The exact
    // `fields` contract is best-effort — if this Whisparr build rejects it the connect flow still succeeds
    // via the copy-paste URL. `method` value 1 = POST (Servarr WebhookMethod enum). The secret is delivered
    // as the `X-Cove-Token` HEADER (verified delivered live): Whisparr's Test ping posts to the URL with the
    // configured headers, so the receiver sees the token and the ping succeeds. The bare URL keeps its
    // `?token=` for the copy-paste path, so both channels authenticate.
    // Exposed internally so the register-payload contract is unit-testable host-free.
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

    // The secret is embedded in the URL's `?token=` query (WebhookUrlBuilder); lift it back out so the header
    // carries the identical value the receiver validates. Returns empty when no token query is present.
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
