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
    /// Imports a Cove-owned file into the <paramref name="scene"/> Eros movie via a targeted <c>ManualImport</c>
    /// with <c>importMode:"copy"</c> — the movie-shaped analogue of the v2 episode import, but copy-not-move so
    /// Cove's own file is NEVER relocated (Whisparr gets its own copy, a hardlink on a shared volume when Whisparr's
    /// "Use Hard Links" setting is on). Never grabs. The file must sit where Whisparr can see it (shared storage).
    /// </summary>
    /// <remarks>
    /// The quality + languages MUST come from the <c>manualimport</c> listing verbatim (a synthesized quality does
    /// not import); the row is matched to the owned file by path (separator/case-normalized). The command completes
    /// async, so success is confirmed by re-reading the movie's <c>hasFile</c>; a queued-but-unlinked outcome is
    /// Unreachable, never a false Ok. Targets a movie that already exists in Whisparr as a fileless row (the caller
    /// only offers fileless scenes) and reads it back by its StashDB id.
    /// </remarks>
    public async Task<WhisparrResult<bool>> ImportOwnedSceneAsync(
        string baseUrl, string apiKey, WhisparrMovie scene, string whisparrFilePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(scene.StashId))
        {
            return WhisparrResult<bool>.Unreachable("v3 owned-import requires the movie's StashDB id for verification");
        }

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
        // quality/languages are the listed objects verbatim — a synthesized quality does not import. `movieId`
        // targets the Eros movie (the v3 analogue of the v2 episodeIds).
        //
        // importMode "copy" (NOT "move"/"auto") is the safety-critical choice: an Eros movie has its OWN per-movie
        // folder, which almost never IS the folder Cove keeps the file in, so a move would relocate Cove's own file
        // and break Cove's library path. "copy" leaves Cove's original exactly where it is and gives Whisparr its
        // own copy — and with Whisparr's "Use Hard Links" setting on a shared volume that copy is a hardlink (one
        // inode, no extra disk). This is the COORD-01 direction; a smarter same-folder in-place path is a later
        // refinement, but it must never regress to moving Cove's file.
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

        for (var attempt = 1; attempt <= MonitorVerifyMaxAttempts; attempt++)
        {
            await Task.Delay(_monitorSettleDelay, ct);

            var reread = await client.GetMovieByStashIdAsync(baseUrl, apiKey, scene.StashId, ct);
            if (!reread.IsOk)
            {
                return Propagate<WhisparrMovie[], bool>(reread);
            }

            if (Array.Find(reread.Value!, m => m.Id == scene.Id) is { HasFile: true })
            {
                return WhisparrResult<bool>.Ok(true);
            }
        }

        return WhisparrResult<bool>.Unreachable("ManualImport queued but movie not linked");
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
            return flip;
        }

        // Scope cascade over the attributed scenes. A bulk MONITOR toggle only (PUT /movie/editor) — it never
        // searches, so loop-safety holds. A cascade failure propagates: the container flip is durable but the
        // caller must know the scenes did not follow.
        var cascade = await CascadeSceneMonitorAsync(baseUrl, apiKey, kind, stashId, monitored, scope, ct);
        return cascade.IsOk ? flip : Propagate<bool, EntityMonitorResult>(cascade);
    }

    // Apply the AllScenes scope to the entity's existing attributed scenes (movie rows): mark every one wanted
    // via the bulk monitor toggle (PUT /movie/editor), which never searches — loop-safety holds. Fires only when
    // turning monitoring ON with AllScenes; NewReleases and OFF are no-ops (the monitored container acquires
    // future scenes on its own, and — like Whisparr's own unmonitor — turning the container off leaves the
    // scene-level monitored flags alone). A brand-new studio has no movie rows yet (Whisparr fetches them
    // asynchronously), so this cascades over whatever rows already exist — the "add all missing" flow populates them.
    private async Task<WhisparrResult<bool>> CascadeSceneMonitorAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, bool monitored, MonitorScope scope, CancellationToken ct)
    {
        if (!monitored || scope != MonitorScope.AllScenes)
        {
            return WhisparrResult<bool>.Ok(true);
        }

        var idsResult = await ListAttributedMovieIdsAsync(baseUrl, apiKey, kind, stashId, monitoredOnly: false, ct);
        if (!idsResult.IsOk)
        {
            return Propagate<int[], bool>(idsResult);
        }

        var ids = idsResult.Value!;
        if (ids.Length == 0)
        {
            return WhisparrResult<bool>.Ok(true);
        }

        var body = JsonSerializer.Serialize(new { movieIds = ids, monitored = true });
        return await client.BulkMonitorMoviesAsync(baseUrl, apiKey, body, ct);
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

    // Interactive grab: POST the guid+indexerId pair to grab THIS release. A distinct single-shot verb.
    public async Task<WhisparrResult<bool>> GrabReleaseAsync(
        string baseUrl, string apiKey, string guid, int indexerId, CancellationToken ct)
    {
        var result = await client.GrabReleaseAsync(baseUrl, apiKey, BuildGrabReleaseBody(guid, indexerId), ct);
        return result.IsOk ? WhisparrResult<bool>.Ok(true) : Propagate<bool, bool>(result);
    }

    // Upgrade search: the SAME MoviesSearch command as a scene search. Whisparr grabs an upgrade only for a
    // movie that is monitored with its cutoff unmet, so eligibility is decided server-side and the upgrade
    // path needs no distinct wire shape. A distinct grab verb; an empty id set issues NO command (Ok no-op).
    public Task<WhisparrResult<BulkActionResult>> SearchForUpgradesAsync(
        string baseUrl, string apiKey, IReadOnlyList<int> movieIds, CancellationToken ct)
        => SearchScenesAsync(baseUrl, apiKey, movieIds, ct);

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

    // Attribution: resolve the entity, then filter the existing movie set by the SAME predicate the
    // status uses (studio by title, performer by foreign id), optionally to monitored. No StashDB call.
    public async Task<WhisparrResult<int[]>> ListAttributedMovieIdsAsync(
        string baseUrl, string apiKey, EntityKind kind, string stashId, bool monitoredOnly, CancellationToken ct)
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
                : await CollectAttributedIdsAsync(baseUrl, apiKey, StudioAttribution(studio), monitoredOnly, ct);
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

        return await CollectAttributedIdsAsync(baseUrl, apiKey, PerformerAttribution(stashId), monitoredOnly, ct);
    }

    // The attributed-id collector: read the movie set and project the ids matching the predicate (optionally
    // monitored). Unlike the status count (which degrades a failed movie read to 0-of-0), a bulk search MUST
    // act on the real set, so a non-Ok movie list propagates rather than silently searching a partial set.
    private async Task<WhisparrResult<int[]>> CollectAttributedIdsAsync(
        string baseUrl, string apiKey, Func<WhisparrMovie, bool> attributed, bool monitoredOnly, CancellationToken ct)
    {
        var moviesResult = await client.ListMoviesAsync(baseUrl, apiKey, ct);
        if (!moviesResult.IsOk)
        {
            return Propagate<WhisparrMovie[], int[]>(moviesResult);
        }

        var ids = moviesResult.Value!
            .Where(attributed)
            .Where(m => !monitoredOnly || m.Monitored)
            .Select(m => m.Id)
            .ToArray();
        return WhisparrResult<int[]>.Ok(ids);
    }

    // Studio add-then-flip. The `?stashId=` query answers an ABSENT studio with an empty array
    // (Ok, never an error), so absence is "no matching row" here — not a 404. On ON+absent: POST monitored:false
    // (a 409/exists re-reads to the existing row, added:false, never a duplicate), then PUT the requested state.
    // On OFF: PUT monitored:false only when present; an absent studio is already unmonitored (no add, no delete).
    private async Task<WhisparrResult<EntityMonitorResult>> SetStudioMonitorAsync(
        string baseUrl, string apiKey, string stashId, bool monitored,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var getResult = await client.GetStudioByStashIdAsync(baseUrl, apiKey, stashId, ct);
        if (!getResult.IsOk)
        {
            return Propagate<WhisparrStudio[], EntityMonitorResult>(getResult);
        }

        var existing = getResult.Value!.FirstOrDefault();
        var added = false;

        if (existing is null)
        {
            if (!monitored)
            {
                // OFF on an absent studio: nothing to unmonitor, nothing to add/delete.
                return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(Added: false, Monitored: false));
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
                    return Propagate<WhisparrStudio[], EntityMonitorResult>(reread);
                }

                existing = reread.Value!.FirstOrDefault();
                if (existing is null)
                {
                    return WhisparrResult<EntityMonitorResult>.Unreachable("studio create conflicted but no row on re-read");
                }
            }
            else if (createResult.IsOk)
            {
                existing = createResult.Value!;
                added = true;
            }
            else
            {
                return Propagate<WhisparrStudio, EntityMonitorResult>(createResult);
            }
        }
        else if (!monitored && !existing.Monitored)
        {
            // OFF on an already-unmonitored studio: idempotent no-op, no redundant PUT.
            return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(added, Monitored: false));
        }

        var putResult = await client.UpdateStudioAsync(
            baseUrl, apiKey, existing.Id, BuildStudioFlipBody(existing, monitored), ct);
        if (!putResult.IsOk)
        {
            return Propagate<WhisparrStudio, EntityMonitorResult>(putResult);
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
                ? WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(added, verified.Value))
                : Propagate<bool, EntityMonitorResult>(verified);
        }

        return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(added, monitored));
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
    private async Task<WhisparrResult<EntityMonitorResult>> SetPerformerMonitorAsync(
        string baseUrl, string apiKey, string stashId, bool monitored,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var getResult = await client.GetPerformerByStashIdAsync(baseUrl, apiKey, stashId, ct);
        if (getResult.State is not (WhisparrResultState.Ok or WhisparrResultState.Absent))
        {
            return Propagate<WhisparrPerformer, EntityMonitorResult>(getResult);
        }

        var existing = getResult.State == WhisparrResultState.Ok ? getResult.Value : null;
        var added = false;

        if (existing is null)
        {
            if (!monitored)
            {
                return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(Added: false, Monitored: false));
            }

            var createResult = await client.CreatePerformerAsync(
                baseUrl, apiKey,
                BuildPerformerAddBody(stashId, rootFolderPath, qualityProfileId, tagIds), ct);

            if (createResult.State == WhisparrResultState.Conflict)
            {
                var reread = await client.GetPerformerByStashIdAsync(baseUrl, apiKey, stashId, ct);
                if (!reread.IsOk)
                {
                    return Propagate<WhisparrPerformer, EntityMonitorResult>(reread);
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
                return Propagate<WhisparrPerformer, EntityMonitorResult>(createResult);
            }
        }
        else if (!monitored && !existing.Monitored)
        {
            return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(added, Monitored: false));
        }

        var putResult = await client.UpdatePerformerAsync(
            baseUrl, apiKey, existing.Id, BuildPerformerFlipBody(existing, monitored), ct);
        if (!putResult.IsOk)
        {
            return Propagate<WhisparrPerformer, EntityMonitorResult>(putResult);
        }

        return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(added, monitored));
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
    private static string BuildGrabReleaseBody(string guid, int indexerId)
        => JsonSerializer.Serialize(new { guid, indexerId });

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
