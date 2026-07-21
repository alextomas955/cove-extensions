using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.Scene;
using WhisparrSync.State;

namespace WhisparrSync.Push;

/// <summary>
/// The host-free orchestration seam for the scene-level Whisparr operations, mirroring
/// <see cref="EntityMonitor"/>: it selects the version adapter, ensures the
/// <c>cove-sync</c> origin tag + resolves the root-folder path before any add (via the shared
/// <see cref="AddContextResolver"/>), and delegates the wire work to the adapter — so the endpoints
/// call ONE method and hold no wire/loop-safety semantics.
///
/// CRITICAL loop-safety: every add path (per-scene add, the monitor add leg, register-owned, and
/// each add-all-missing registration) passes <c>searchForMovie:false</c> and issues no command — only
/// <see cref="SearchSceneAsync"/> / <see cref="SearchAllMonitoredAsync"/> can grab. The missing set is
/// a LOCAL diff of the entity's own Cove scenes against the already-fetched Whisparr movie set — never a
/// StashDB call.
///
/// Constructor-injected with the transport client + the already-loaded options + the Cove-library port, so it
/// unit-tests against a fake HTTP handler and a fake port with no host. Host-free: the endpoint supplies the
/// loaded options and a scoped <see cref="ICoveLibraryPort"/>.
/// </summary>
internal sealed class SceneActions(WhisparrClient client, WhisparrOptions options, ICoveLibraryPort library)
{
    // The shared origin-tag-ensure + root-folder-resolve concern, single-sourced with the
    // entity monitor. Constructed per SceneActions instance so its tag-id cache lives for one operation.
    private readonly AddContextResolver _addContext = new(client, options);

    /// <summary>
    /// Adds a scene to Whisparr without grabbing. Ensures the origin tag (+ any add-defaults extra
    /// tags) + resolves the root, then delegates <c>AddSceneAsync(searchForMovie:false)</c> with the
    /// <see cref="WhisparrOptions.MonitorNewByDefault"/> monitored choice. A repeat add of a present scene is
    /// idempotent (409/exists = success, <c>Added:false</c>).
    /// </summary>
    internal Task<WhisparrResult<SceneActionResult>> AddSceneAsync(string stashId, string? title, CancellationToken ct)
        => AddWithMonitorAsync(stashId, title, monitored: options.MonitorNewByDefault, ct);

    /// <summary>
    /// Sets a scene's monitor state via the adapter's add-then-flip. When turning monitor ON it
    /// ensures the origin tag + resolves the root first (the add leg may run for an absent scene); when
    /// turning OFF it delegates a bare unmonitor (no tag/root work, no add). Never searches.
    /// </summary>
    internal async Task<WhisparrResult<SceneActionResult>> SetSceneMonitorAsync(
        string stashId, string? title, bool monitored, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null || !adapter.SupportsSceneAdd)
        {
            // A per-scene monitor's add leg needs a per-scene add; a version without one (v2) defers here,
            // BEFORE the root + origin-tag resolve, so a deferred toggle issues no stray wire call.
            return WhisparrResult<SceneActionResult>.VersionMismatch(options.DetectedVersion);
        }

        var rootFolderPath = string.Empty;
        IReadOnlyList<int> tagIds = [];

        if (monitored)
        {
            // Only the add leg (absent + ON) needs the root + origin tag; OFF skips this work entirely.
            var context = await ResolveAddContextAsync(ct);
            if (!context.IsOk)
            {
                return Propagate<AddContext, SceneActionResult>(context);
            }

            rootFolderPath = context.Value!.RootFolderPath;
            tagIds = context.Value.TagIds;
        }

        return await adapter.SetSceneMonitorAsync(
            options.BaseUrl, options.ApiKey, stashId, title, monitored,
            rootFolderPath, options.QualityProfileId, tagIds, ct);
    }

    /// <summary>
    /// Searches now for a single scene by posting one <c>MoviesSearch</c> over its Whisparr
    /// <paramref name="movieId"/> — the sole per-scene grab path.
    /// </summary>
    internal Task<WhisparrResult<BulkActionResult>> SearchSceneAsync(int movieId, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        return adapter is null
            ? Task.FromResult(WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion))
            : adapter.SearchScenesAsync(options.BaseUrl, options.ApiKey, [movieId], ct);
    }

    /// <summary>
    /// Searches all monitored scenes of a studio/performer — resolves the entity's monitored
    /// attributed movie ids from the already-fetched Whisparr movie set (no StashDB call) and posts one
    /// <c>MoviesSearch</c> over them. An entity with no monitored attributed movie sends no command.
    /// </summary>
    internal async Task<WhisparrResult<BulkActionResult>> SearchAllMonitoredAsync(
        EntityKind kind, string stashId, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null)
        {
            return WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion);
        }

        var idsResult = await adapter.ListAttributedMovieIdsAsync(
            options.BaseUrl, options.ApiKey, kind, stashId, monitoredOnly: true, ct);
        if (!idsResult.IsOk)
        {
            return Propagate<int[], BulkActionResult>(idsResult);
        }

        return await adapter.SearchScenesAsync(options.BaseUrl, options.ApiKey, idsResult.Value!, ct);
    }

    /// <summary>
    /// Adds a Whisparr import-list exclusion for a scene by its StashDB id. v3-only (defers
    /// VersionMismatch on v2 BEFORE any wire call). Idempotent — re-excluding a scene is an Ok success, never a
    /// duplicate. Issues no search/command (an exclusion never grabs).
    /// </summary>
    internal Task<WhisparrResult<bool>> ExcludeSceneAsync(string stashId, string? title, int? year, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        return adapter is null
            ? Task.FromResult(WhisparrResult<bool>.VersionMismatch(options.DetectedVersion))
            : adapter.AddExclusionAsync(options.BaseUrl, options.ApiKey, stashId, title, year, ct);
    }

    /// <summary>
    /// Removes a scene's import-list exclusion by its StashDB id. v3-only. The adapter resolves the
    /// exclusion's Whisparr id server-side (foreignId match — never a caller id); a not-excluded scene is an Ok
    /// no-op. Issues no search/command.
    /// </summary>
    internal Task<WhisparrResult<bool>> UnExcludeSceneAsync(string stashId, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        return adapter is null
            ? Task.FromResult(WhisparrResult<bool>.VersionMismatch(options.DetectedVersion))
            : adapter.RemoveExclusionAsync(options.BaseUrl, options.ApiKey, stashId, ct);
    }

    /// <summary>
    /// Grabs one specific indexer release for a scene (the sole interactive grab). v3-only; delegates
    /// the guid+indexerId grab to the adapter. A distinct single-shot grab verb.
    /// </summary>
    internal Task<WhisparrResult<bool>> GrabReleaseAsync(string guid, int indexerId, int movieId, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        return adapter is null
            ? Task.FromResult(WhisparrResult<bool>.VersionMismatch(options.DetectedVersion))
            : adapter.GrabReleaseAsync(options.BaseUrl, options.ApiKey, guid, indexerId, movieId, ct);
    }

    /// <summary>
    /// Searches the given Whisparr movies for a quality upgrade — one grab-capable verb,
    /// distinct from add/exclude. v3-only. Honors <see cref="WhisparrOptions.AllowQualityUpgrades"/>: when the
    /// setting is off this is an Ok no-op that issues NO command; when on it posts the upgrade search (Whisparr
    /// grabs an upgrade only for a monitored movie whose cutoff is unmet). An empty id set issues no command.
    /// </summary>
    internal Task<WhisparrResult<BulkActionResult>> SearchForUpgradesAsync(IReadOnlyList<int> movieIds, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null)
        {
            return Task.FromResult(WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion));
        }

        return options.AllowQualityUpgrades
            ? adapter.SearchForUpgradesAsync(options.BaseUrl, options.ApiKey, movieIds, ct)
            : Task.FromResult(WhisparrResult<BulkActionResult>.Ok(BulkActionResult.Empty));
    }

    /// <summary>: searches a single scene's Whisparr movie for a quality upgrade (see the id-set overload).</summary>
    internal Task<WhisparrResult<BulkActionResult>> SearchForUpgradesAsync(int movieId, CancellationToken ct)
        => SearchForUpgradesAsync([movieId], ct);

    /// <summary>
    /// Batch "Add to Whisparr": registers each selected scene (idempotent, origin-tagged,
    /// <c>searchForMovie:false</c> — never grabs), aggregating a <see cref="BulkActionResult"/>. Resolves the
    /// origin tag + add-defaults tags + root ONCE for the batch, then reuses the per-scene add path.
    /// v3-only (defers VersionMismatch BEFORE any wire call).
    /// </summary>
    internal async Task<WhisparrResult<BulkActionResult>> AddScenesAsync(IReadOnlyList<SceneRef> scenes, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null || !adapter.SupportsSceneAdd)
        {
            return WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion);
        }

        if (scenes.Count == 0)
        {
            return WhisparrResult<BulkActionResult>.Ok(BulkActionResult.Empty);
        }

        var context = await ResolveAddContextAsync(ct);
        if (!context.IsOk)
        {
            return Propagate<AddContext, BulkActionResult>(context);
        }

        int succeeded = 0, failed = 0;
        foreach (var scene in scenes)
        {
            var result = await adapter.AddSceneAsync(
                options.BaseUrl, options.ApiKey, scene.StashId, scene.Title,
                monitored: options.MonitorNewByDefault, searchForMovie: false,
                context.Value!.RootFolderPath, options.QualityProfileId, context.Value.TagIds, ct);
            if (result.IsOk)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }
        }

        return WhisparrResult<BulkActionResult>.Ok(new BulkActionResult(scenes.Count, succeeded, failed));
    }

    /// <summary>
    /// Batch: excludes each selected scene, aggregating a <see cref="BulkActionResult"/> (idempotent
    /// per scene). v3-only (defers BEFORE any wire call). Issues no search/command.
    /// </summary>
    internal async Task<WhisparrResult<BulkActionResult>> ExcludeScenesAsync(IReadOnlyList<SceneRef> scenes, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null)
        {
            return WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion);
        }

        int succeeded = 0, failed = 0;
        foreach (var scene in scenes)
        {
            var result = await adapter.AddExclusionAsync(
                options.BaseUrl, options.ApiKey, scene.StashId, scene.Title, scene.Year, ct);
            if (result.State == WhisparrResultState.VersionMismatch)
            {
                // A version with no exclusion surface (v2) defers the whole batch — not N per-item failures.
                return Propagate<bool, BulkActionResult>(result);
            }

            if (result.IsOk)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }
        }

        return WhisparrResult<BulkActionResult>.Ok(new BulkActionResult(scenes.Count, succeeded, failed));
    }

    /// <summary>
    /// Registers every scene of an entity that Cove owns but Whisparr does not yet track. Enumerates
    /// the entity's OWN Cove scenes (via the library port), diffs their StashDB ids against the fetched Whisparr
    /// movie set locally (<see cref="SceneStatusProjector.BuildMovieIndex"/>), and RegisterOwned-adds each
    /// MISSING one (<c>monitored:false</c>, <c>searchForMovie:false</c>, origin-tagged). This is a LOCAL diff —
    /// NO StashDB / stashbox call. Idempotent: a re-run finds the now-present scenes and skips them, adding no
    /// duplicate. The result's <c>Total</c> is the number of missing scenes attempted (present scenes are not
    /// counted work), <c>Succeeded</c> those registered, <c>Failed</c> those the adapter rejected.
    /// </summary>
    internal async Task<WhisparrResult<BulkActionResult>> AddAllMissingAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null || !adapter.SupportsSceneAdd)
        {
            // No per-scene add (v2) means nothing to register — defer BEFORE the movie read + the port
            // enumeration so a deferred bulk-add issues no wire call and reads no Cove scenes.
            return WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion);
        }

        // The already-fetched Whisparr movie set is the WHOLE diff counterpart — no StashDB egress.
        var moviesResult = await adapter.ListMoviesAsync(options.BaseUrl, options.ApiKey, ct);
        if (!moviesResult.IsOk)
        {
            return Propagate<WhisparrMovie[], BulkActionResult>(moviesResult);
        }

        var movieIndex = SceneStatusProjector.BuildMovieIndex(moviesResult.Value!);
        var scenes = await library.LoadVideosForEntityAsync(kind, coveEntityId, ct);

        // A scene is MISSING when it carries a usable StashDB id AND none of its ids index a Whisparr movie.
        // Keep the whole CoveVideo alongside its id so each add carries a Cove-derived title (Bug D): a null/
        // empty title is rejected by Whisparr Eros, so a title MUST be resolved from what Cove has per scene.
        var missing = scenes
            .Select(v => (Video: v, StashId: v.StashIds.FirstOrDefault(id => !string.IsNullOrEmpty(id))))
            .Where(x => x.StashId is not null && !movieIndex.ContainsKey(x.StashId))
            .GroupBy(x => x.StashId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (missing.Count == 0)
        {
            return WhisparrResult<BulkActionResult>.Ok(BulkActionResult.Empty);
        }

        // Resolve the origin tag + root ONCE for the whole batch (the resolver caches the tag id).
        var context = await ResolveAddContextAsync(ct);
        if (!context.IsOk)
        {
            return Propagate<AddContext, BulkActionResult>(context);
        }

        int succeeded = 0, failed = 0;
        foreach (var (video, missingStashId) in missing)
        {
            var addResult = await adapter.AddSceneAsync(
                options.BaseUrl, options.ApiKey, missingStashId!,
                ResolveTitle(video.Title, video.FilePaths, missingStashId!),
                monitored: false, searchForMovie: false,
                context.Value!.RootFolderPath, options.QualityProfileId, context.Value.TagIds, ct);
            if (addResult.IsOk)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }
        }

        return WhisparrResult<BulkActionResult>.Ok(new BulkActionResult(missing.Count, succeeded, failed));
    }

    /// <summary>
    /// Imports every scene an entity OWNS in Cove into Whisparr: for each Cove video that carries the connected
    /// version's identity id (StashDB on v3, ThePornDB on v2) AND a file path AND matches a FILELESS Whisparr scene
    /// by that id, attaches the owned file WITHOUT moving or deleting Cove's own file and without searching. On v3
    /// a scene alone in its own folder is adopted IN PLACE (the movie row's path is re-pointed to Cove's folder +
    /// a rescan, zero duplication); a scene sharing its folder with another owned scene (a flat layout) falls back
    /// to a copy import and the result carries a <see cref="BulkActionResult.Message"/> explaining the fall-back.
    /// v2 registers its episode in place. The Whisparr scene set comes from <see cref="IWhisparrAdapter.ListMoviesAsync"/>
    /// (v3 movies keyed by StashDB id; v2 synthesized v2scenes keyed by TPDB id and carrying <c>SeriesId</c>);
    /// only FILELESS scenes are indexed, so a scene Whisparr already has is skipped (loop-safe, no re-grab). The
    /// scene must already exist in Whisparr — the caller registers missing scenes first ("Add all missing",
    /// non-grabbing). <see cref="BulkActionResult.Total"/> is the owned+matched scenes attempted,
    /// <see cref="BulkActionResult.Succeeded"/> those that imported, <see cref="BulkActionResult.Failed"/> the rest;
    /// an empty set is <see cref="BulkActionResult.Empty"/>.
    /// </summary>
    internal async Task<WhisparrResult<BulkActionResult>> ReflectOwnedAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null || !adapter.SupportsOwnedImport)
        {
            // An unmanageable version has no adapter — defer before any read.
            return WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion);
        }

        var moviesResult = await adapter.ListMoviesAsync(options.BaseUrl, options.ApiKey, ct);
        if (!moviesResult.IsOk)
        {
            return Propagate<WhisparrMovie[], BulkActionResult>(moviesResult);
        }

        // The owned-import identity id follows the connected version: v3 (Eros) keys on the StashDB id carried in
        // WhisparrMovie.StashId; v2 (Sonarr) on the ThePornDB id carried in ForeignId (with SeriesId the site the
        // targeted ManualImport attaches to). Skipping hasFile scenes keeps the import loop-safe: a scene Whisparr
        // already has is never re-imported. First row wins on a duplicate id.
        var isV2 = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
        var filelessById = new Dictionary<string, WhisparrMovie>(StringComparer.OrdinalIgnoreCase);
        foreach (var movie in moviesResult.Value!)
        {
            if (movie.HasFile)
            {
                continue;
            }

            var key = isV2 ? movie.ForeignId : movie.StashId;
            if (!string.IsNullOrEmpty(key) && (!isV2 || movie.SeriesId is not null))
            {
                filelessById.TryAdd(key, movie);
            }
        }

        var videos = await library.LoadVideosForEntityAsync(kind, coveEntityId, ct);

        // Collect each owned+matched scene (a Cove file whose id matches a fileless Whisparr scene) with the
        // normalized parent directory of its file, so the layout can be classified before any import is issued.
        var matched = new List<(WhisparrMovie Scene, string FilePath, string ParentDir)>();
        foreach (var video in videos)
        {
            var ids = isV2 ? video.TpdbIds : video.StashIds;
            var id = ids.FirstOrDefault(x => !string.IsNullOrEmpty(x));
            var filePath = video.FilePaths.FirstOrDefault(p => !string.IsNullOrEmpty(p));
            if (id is null || filePath is null || !filelessById.TryGetValue(id, out var scene))
            {
                continue;
            }

            matched.Add((scene, filePath, ParentDir(filePath)));
        }

        if (matched.Count == 0)
        {
            return WhisparrResult<BulkActionResult>.Ok(BulkActionResult.Empty);
        }

        // Folder-per-scene guard (v3): Whisparr's MoviePathValidator rejects two movies sharing a path, and a
        // rescan on a shared directory would link the wrong sibling — so a scene sharing its parent dir with
        // another owned scene falls back to the copy import; a scene alone in its folder is adopted in place (zero
        // duplication). Grouping is over EventLedger.NormalizePath (case-sensitive, the Linux/Docker rule the root
        // guard uses), never a raw compare. v2 attaches under the series folder regardless, so it is exempt.
        var scenesPerDir = matched
            .GroupBy(m => m.ParentDir, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        int total = 0, succeeded = 0, failed = 0;
        var flatFallback = false;
        foreach (var (scene, filePath, parentDir) in matched)
        {
            total++;
            var mode = OwnedImportMode.InPlaceAdopt;
            if (!isV2 && scenesPerDir[parentDir] > 1)
            {
                mode = OwnedImportMode.Copy;
                flatFallback = true;
            }

            // Cove and Whisparr must see the library at the same path (the import webhook already requires it),
            // so the owned scene's Cove path IS its Whisparr path — no translation.
            var import = await adapter.ImportOwnedSceneAsync(
                options.BaseUrl, options.ApiKey, scene, filePath, mode, ct);
            if (import.IsOk)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }
        }

        var message = flatFallback
            ? "Some scenes share a folder (a flat library layout), so they were imported by copy instead of adopted in place."
            : null;
        return WhisparrResult<BulkActionResult>.Ok(new BulkActionResult(total, succeeded, failed, message));
    }

    // The normalized parent directory of a file path — the folder-per-scene grouping key. Reuses the shared
    // EventLedger.NormalizePath (separators unified, trailing slash trimmed, case-SENSITIVE) so the layout
    // comparison stays consistent with the root-containment rule; never a raw string compare.
    private static string ParentDir(string filePath)
    {
        var normalized = EventLedger.NormalizePath(filePath);
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash <= 0 ? normalized : normalized[..lastSlash];
    }

    /// <summary>
    /// Resolves a NON-EMPTY movie title from what Cove has for a scene (Whisparr Eros
    /// rejects an add whose title is empty): the Cove scene <paramref name="coveTitle"/> when non-blank, else
    /// the basename of the first Cove <paramref name="filePaths"/> entry, else a stable <c>Scene {stashId}</c>.
    /// Shared by the per-scene add endpoints and the bulk add-all-missing loop so every add path derives its
    /// title by the same rule. Never returns null/empty.
    /// </summary>
    internal static string ResolveTitle(string? coveTitle, IReadOnlyList<string>? filePaths, string stashId)
    {
        if (!string.IsNullOrWhiteSpace(coveTitle))
        {
            return coveTitle;
        }

        var firstPath = filePaths?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (firstPath is not null)
        {
            // Cove file paths are forward-slash denormalized, but tolerate a back-slash too — take the last segment.
            var cut = Math.Max(firstPath.LastIndexOf('/'), firstPath.LastIndexOf('\\'));
            var name = cut >= 0 ? firstPath[(cut + 1)..] : firstPath;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return $"Scene {stashId}";
    }

    // The shared add path for AddScene (monitored:true) and RegisterOwned (monitored:false): select the
    // adapter, resolve the origin tag + root, then AddSceneAsync with searchForMovie:FALSE (never grabs).
    private async Task<WhisparrResult<SceneActionResult>> AddWithMonitorAsync(
        string stashId, string? title, bool monitored, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null || !adapter.SupportsSceneAdd)
        {
            // Defer a version with no per-scene add (v2) BEFORE the root + origin-tag resolve — no stray call.
            return WhisparrResult<SceneActionResult>.VersionMismatch(options.DetectedVersion);
        }

        var context = await ResolveAddContextAsync(ct);
        if (!context.IsOk)
        {
            return Propagate<AddContext, SceneActionResult>(context);
        }

        return await adapter.AddSceneAsync(
            options.BaseUrl, options.ApiKey, stashId, title, monitored, searchForMovie: false,
            context.Value!.RootFolderPath, options.QualityProfileId, context.Value.TagIds, ct);
    }

    // The version adapter for the connected instance, or null when the version is unmanageable. On v2 the
    // studio search-all path (episode search) GOes; the per-scene ADD paths still defer, gated on
    // adapter.SupportsSceneAdd BEFORE resolving the root + origin tag so a deferred v2 add stays wire-free
    // (no stray tag/root call). The V2Adapter's own scene deferral (SceneAdapterTests) stands as the
    // direct-call contract.
    private IWhisparrAdapter? SelectAdapter()
        => AdapterSelector.SelectForVersion(options.SelectedVersion, client);

    // Resolve the two add prerequisites in one shot: the root path + the origin tag id. Every add path here
    // (scene-add, scene-monitor, bulk-add-missing) registers a scene Whisparr does NOT own yet, so there is no
    // owned file to prefix-match — the file-less fallback root is the right derivation (§Pitfall 1; the owned
    // file's real path is handled by the in-place adopt in ReflectOwnedAsync, not by an add's root). Either
    // resolve failing propagates verbatim so a bad key / unreachable / no-root surfaces to the caller.
    private async Task<WhisparrResult<AddContext>> ResolveAddContextAsync(CancellationToken ct)
    {
        var rootResult = await _addContext.ResolveFallbackRootAsync(ct);
        if (!rootResult.IsOk)
        {
            return Propagate<string, AddContext>(rootResult);
        }

        var tagResult = await _addContext.EnsureTagIdsAsync(options.TagsOnAdd, ct);
        if (!tagResult.IsOk)
        {
            return Propagate<IReadOnlyList<int>, AddContext>(tagResult);
        }

        return WhisparrResult<AddContext>.Ok(new AddContext(rootResult.Value!, tagResult.Value!));
    }

    // The resolved add prerequisites (root path + origin tag id set) carried between resolve and add.
    private sealed record AddContext(string RootFolderPath, IReadOnlyList<int> TagIds);

    // Re-shape a non-Ok result of one payload type into the same state for the caller's return type.
    private static WhisparrResult<TTo> Propagate<TFrom, TTo>(WhisparrResult<TFrom> source)
        => WhisparrResult<TTo>.PropagateFrom(source);
}
