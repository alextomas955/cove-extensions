using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.SceneStatus;
using WhisparrSync.State;

namespace WhisparrSync.Push;

/// <summary>
/// The host-free orchestration seam for the scene-level Whisparr operations, mirroring
/// <see cref="EntityMonitor"/>: it selects the version adapter, ensures the
/// <c>cove-sync</c> origin tag + resolves the root-folder path and the quality profile before any add (via the
/// shared <see cref="AddContextResolver"/>), and delegates the wire work to the adapter — so the endpoints
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
internal sealed class SceneActions(
    WhisparrClient client, WhisparrOptions options, ICoveLibraryPort library, WhisparrCapabilityPort capability)
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
        if (SelectAdapter() is not IWhisparrScenePush push)
        {
            // A per-scene monitor's add leg needs a per-scene add; a version without one (v2 lacks
            // IWhisparrScenePush) defers here, BEFORE the root + origin-tag resolve, so a deferred toggle issues
            // no stray wire call.
            return WhisparrResult<SceneActionResult>.VersionMismatch(options.DetectedVersion);
        }

        var rootFolderPath = string.Empty;
        int qualityProfileId = 0;
        IReadOnlyList<int> tagIds = [];

        if (monitored)
        {
            // Only the add leg (absent + ON) needs the root + profile + origin tag; OFF skips this work entirely
            // and the unresolved profile reaches only a flip, which echoes the existing movie's own.
            var context = await ResolveAddContextAsync(ct);
            if (!context.IsOk)
            {
                return Propagate<AddContext, SceneActionResult>(context);
            }

            rootFolderPath = context.Value!.RootFolderPath;
            qualityProfileId = context.Value.QualityProfileId;
            tagIds = context.Value.TagIds;
        }

        return await push.SetSceneMonitorAsync(
            options.BaseUrl, options.ApiKey, stashId, title, monitored,
            rootFolderPath, qualityProfileId, tagIds, ct);
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

        // Studio attribution rides the aggregate; a performer target must hold IWhisparrPerformerMonitor (v3),
        // else the search-all defers exactly as the old performer VersionMismatch did.
        var idsResult = kind == EntityKind.Studio
            ? await adapter.ListStudioAttributedIdsAsync(options.BaseUrl, options.ApiKey, stashId, monitoredOnly: true, ct)
            : adapter is IWhisparrPerformerMonitor performer
                ? await performer.ListPerformerAttributedIdsAsync(options.BaseUrl, options.ApiKey, stashId, monitoredOnly: true, ct)
                : WhisparrResult<int[]>.VersionMismatch(options.DetectedVersion);
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
        return SelectAdapter() is IWhisparrExclusions exclusions
            ? exclusions.AddExclusionAsync(options.BaseUrl, options.ApiKey, stashId, title, year, ct)
            : Task.FromResult(WhisparrResult<bool>.VersionMismatch(options.DetectedVersion));
    }

    /// <summary>
    /// Removes a scene's import-list exclusion by its StashDB id. v3-only. The adapter resolves the
    /// exclusion's Whisparr id server-side (foreignId match — never a caller id); a not-excluded scene is an Ok
    /// no-op. Issues no search/command.
    /// </summary>
    internal Task<WhisparrResult<bool>> UnExcludeSceneAsync(string stashId, CancellationToken ct)
    {
        return SelectAdapter() is IWhisparrExclusions exclusions
            ? exclusions.RemoveExclusionAsync(options.BaseUrl, options.ApiKey, stashId, ct)
            : Task.FromResult(WhisparrResult<bool>.VersionMismatch(options.DetectedVersion));
    }

    /// <summary>
    /// Grabs one specific indexer release for a scene (the sole interactive grab). v3-only; delegates
    /// the guid+indexerId grab to the adapter. A distinct single-shot grab verb.
    /// </summary>
    internal Task<WhisparrResult<bool>> GrabReleaseAsync(string guid, int indexerId, int movieId, CancellationToken ct)
    {
        return SelectAdapter() is IWhisparrReleaseGrab releaseGrab
            ? releaseGrab.GrabReleaseAsync(options.BaseUrl, options.ApiKey, guid, indexerId, movieId, ct)
            : Task.FromResult(WhisparrResult<bool>.VersionMismatch(options.DetectedVersion));
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

        // The setting-off no-op precedes the version gate: an off-setting is an Ok(Empty) on EITHER version
        // (no command issued), and only an on-setting reaches the v3-only upgrade grab.
        if (!options.AllowQualityUpgrades)
        {
            return Task.FromResult(WhisparrResult<BulkActionResult>.Ok(BulkActionResult.Empty));
        }

        return adapter is IWhisparrReleaseGrab releaseGrab
            ? releaseGrab.SearchForUpgradesAsync(options.BaseUrl, options.ApiKey, movieIds, ct)
            : Task.FromResult(WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion));
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
    internal Task<WhisparrResult<BulkActionResult>> AddScenesAsync(IReadOnlyList<SceneRef> scenes, CancellationToken ct)
        => AddScenesInternalAsync(scenes, monitored: options.MonitorNewByDefault, ct);

    /// <summary>
    /// Marks each scene WANTED — ensures it ends up <c>monitored:true</c> in Whisparr so it joins the wanted list
    /// (<c>monitored &amp;&amp; !hasFile</c>) and is acquired on Whisparr's normal search cycle. Delegates to the
    /// shipped per-scene monitor spine (<see cref="SetSceneMonitorAsync"/>) rather than a bare add, because a
    /// discovery "missing" scene from the through-Whisparr path is ALREADY an added-but-unmonitored movie — a bare
    /// add would 409-no-op without flipping it — so the add-then-flip is what actually arms it (an absent scene is
    /// added <c>monitored:false</c> then flipped ON; a present one is flipped by a PUT). Loop-safe: NEVER searches
    /// (<c>searchForMovie:false</c> on any add leg, no command), origin-tagged, idempotent. Only ever called on
    /// NON-owned catalogue scenes (the caller validates the source id against the missing set), so arming
    /// acquisition is the feature, never a re-grab of Cove's owned content. v3-only (defers BEFORE any wire call).
    /// </summary>
    internal async Task<WhisparrResult<BulkActionResult>> MarkScenesWantedAsync(IReadOnlyList<SceneRef> scenes, CancellationToken ct)
    {
        if (SelectAdapter() is not IWhisparrScenePush)
        {
            return WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion);
        }

        if (scenes.Count == 0)
        {
            return WhisparrResult<BulkActionResult>.Ok(BulkActionResult.Empty);
        }

        var tally = new AddTally();
        foreach (var scene in scenes)
        {
            tally.Record(await SetSceneMonitorAsync(scene.StashId, scene.Title, monitored: true, ct));
        }

        return WhisparrResult<BulkActionResult>.Ok(tally.ToResult(scenes.Count));
    }

    // The batch add leg behind AddScenes: resolve the origin tag + add-defaults tags + root ONCE, then loop the
    // per-scene add with searchForMovie:FALSE (never grabs). monitored := the add-defaults choice. There is
    // exactly one add path + one AddContextResolver. v3-only (defers BEFORE any wire call).
    private async Task<WhisparrResult<BulkActionResult>> AddScenesInternalAsync(
        IReadOnlyList<SceneRef> scenes, bool monitored, CancellationToken ct)
    {
        if (SelectAdapter() is not IWhisparrScenePush push)
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

        var tally = new AddTally();
        foreach (var scene in scenes)
        {
            tally.Record(await push.AddSceneAsync(
                options.BaseUrl, options.ApiKey, scene.StashId, scene.Title,
                monitored, searchForMovie: false,
                context.Value!.RootFolderPath, context.Value.QualityProfileId, context.Value.TagIds, ct));
        }

        return WhisparrResult<BulkActionResult>.Ok(tally.ToResult(scenes.Count));
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
            // A version with no exclusion surface (v2 lacks IWhisparrExclusions) defers the whole batch — the
            // first item yields VersionMismatch, matching the old per-item deferral, so an empty set is still Ok.
            var result = adapter is IWhisparrExclusions exclusions
                ? await exclusions.AddExclusionAsync(options.BaseUrl, options.ApiKey, scene.StashId, scene.Title, scene.Year, ct)
                : WhisparrResult<bool>.VersionMismatch(options.DetectedVersion);
            if (result.State == WhisparrResultState.VersionMismatch)
            {
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
    /// counted work), <c>Succeeded</c> those registered, <c>Failed</c> those the adapter rejected; a scene
    /// Whisparr's own import exclusions cover is counted neither way and named in <c>Message</c>.
    /// </summary>
    internal async Task<WhisparrResult<BulkActionResult>> AddAllMissingAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is not IWhisparrScenePush push)
        {
            // No per-scene add (v2 lacks IWhisparrScenePush) means nothing to register — defer BEFORE the movie
            // read + the port enumeration so a deferred bulk-add issues no wire call and reads no Cove scenes.
            return WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion);
        }

        // A tag's owned set is not tag-scoped, so enumerating "this entity's scenes" for a tag is a
        // whole-library read. The UI mounts this only on studio/performer, but the kind arrives in the request
        // body, so refuse it here — classified, before any wire call — rather than let the port throw.
        if (kind == EntityKind.Tag)
        {
            return WhisparrResult<BulkActionResult>.Rejected(
                "Adding every missing scene is available for a studio or performer, not a tag.");
        }

        var catalogue = await EntityCatalogueAsync(kind, coveEntityId, ct);
        if (!catalogue.IsOk)
        {
            return Propagate<EntityCatalogue, BulkActionResult>(catalogue);
        }

        // THE GATE. An entity Whisparr does not know holds no rows, exactly like one it knows that holds none —
        // and collapsing the two makes every scene Cove owns under the entity read as missing, so this pass
        // would register the entity's whole catalogue. The refusal lands here, before the add context resolves
        // and before the first add, so it costs zero outbound writes.
        if (catalogue.Value!.State == EntityCatalogueState.EntityUnknown)
        {
            return WhisparrResult<BulkActionResult>.Rejected(
                $"Whisparr doesn't know this {EntityWord(kind)} yet. Monitor it in Whisparr first, then add its missing scenes.");
        }

        var movieIndex = SceneStatusProjector.BuildMovieIndex(catalogue.Value.Movies);
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

        // Resolve the origin tag + root + profile ONCE for the whole batch (the resolver caches the tag id). A
        // studio target is the parent of every scene registered here, so its Whisparr row's profile is what
        // Whisparr's own studio sync would give them; a performer or tag target spans studios and has no parent.
        var context = await ResolveAddContextAsync(ct, await ParentStudioForeignIdAsync(kind, coveEntityId, ct));
        if (!context.IsOk)
        {
            return Propagate<AddContext, BulkActionResult>(context);
        }

        var tally = new AddTally();
        foreach (var (video, missingStashId) in missing)
        {
            tally.Record(await push.AddSceneAsync(
                options.BaseUrl, options.ApiKey, missingStashId!,
                ResolveTitle(video.Title, video.FilePaths, missingStashId!),
                monitored: false, searchForMovie: false,
                context.Value!.RootFolderPath, context.Value.QualityProfileId, context.Value.TagIds, ct));
        }

        return WhisparrResult<BulkActionResult>.Ok(tally.ToResult(missing.Count));
    }

    /// <summary>
    /// Imports every scene an entity OWNS in Cove into Whisparr: for each Cove video that carries the connected
    /// version's identity id (StashDB on v3, ThePornDB on v2) AND a file path AND matches a FILELESS Whisparr scene
    /// by that id, attaches the owned file WITHOUT moving or deleting Cove's own file and without searching. On v3
    /// a scene alone in its own folder is adopted IN PLACE (the movie row's path is re-pointed to Cove's folder +
    /// a rescan, zero duplication); a scene sharing its folder with another owned scene (a flat layout) falls back
    /// to a copy import and the result carries a <see cref="BulkActionResult.Message"/> explaining the fall-back.
    /// v2 registers its episode in place. The Whisparr scene set comes from <see cref="IWhisparrReconcileSource.ListMoviesAsync"/>
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
        // Same refusal as AddAllMissingAsync, and for the same reason: reflecting "this entity's owned scenes"
        // for a tag is a whole-library enumeration, and the kind arrives in the request body.
        if (kind == EntityKind.Tag)
        {
            return WhisparrResult<BulkActionResult>.Rejected(
                "Reflecting owned scenes is available for a studio or performer, not a tag.");
        }

        var adapter = SelectAdapter();
        if (adapter is null)
        {
            // Owned-import is a shared role (both versions hold IWhisparrOwnedImport); only an unmanageable
            // version has no adapter — defer before any read.
            return WhisparrResult<BulkActionResult>.VersionMismatch(options.DetectedVersion);
        }

        var catalogue = await EntityCatalogueAsync(kind, coveEntityId, ct);
        if (!catalogue.IsOk)
        {
            return Propagate<EntityCatalogue, BulkActionResult>(catalogue);
        }

        WhisparrMovie[] rows;
        switch (catalogue.Value!.State)
        {
            case EntityCatalogueState.NotEnumerableOnThisVersion:
                // A v2 performer or tag. This branch is selected by a DECLARED capability value — the connected
                // generation carries no such entity — never by an exception and never by a non-Ok result, so it
                // is not a fallback-on-failure. There is nothing narrower to ask for here, so the whole-set walk
                // stays rather than the behaviour regressing.
                var wholeSet = await adapter.ListMoviesAsync(options.BaseUrl, options.ApiKey, ct);
                if (!wholeSet.IsOk)
                {
                    return Propagate<WhisparrMovie[], BulkActionResult>(wholeSet);
                }

                rows = wholeSet.Value!;
                break;

            case EntityCatalogueState.EntityUnknown:
                // Unlike the registering pass, this one has nothing to damage: there is no file to import into
                // an entity Whisparr has never heard of. It is a handled empty outcome rather than a refusal, so
                // a sync spanning unregistered entities does not report failures — but it says so, because an
                // empty result with no reason is indistinguishable from one where nothing needed doing.
                return WhisparrResult<BulkActionResult>.Ok(BulkActionResult.Empty with
                {
                    Message = $"Whisparr doesn't know this {EntityWord(kind)}, so there was nothing to import.",
                });

            default:
                rows = catalogue.Value.Movies;
                break;
        }

        // The owned-import identity id follows the connected version: v3 (Eros) keys on the StashDB id carried in
        // WhisparrMovie.StashId; v2 (Sonarr) on the ThePornDB id carried in ForeignId (with SeriesId the site the
        // targeted ManualImport attaches to). Skipping hasFile scenes keeps the import loop-safe: a scene Whisparr
        // already has is never re-imported. First row wins on a duplicate id.
        var isV2 = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
        var filelessById = new Dictionary<string, WhisparrMovie>(StringComparer.OrdinalIgnoreCase);
        foreach (var movie in rows)
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
        if (SelectAdapter() is not IWhisparrScenePush push)
        {
            // Defer a version with no per-scene add (v2 lacks IWhisparrScenePush) BEFORE the root + origin-tag
            // resolve — no stray call.
            return WhisparrResult<SceneActionResult>.VersionMismatch(options.DetectedVersion);
        }

        var context = await ResolveAddContextAsync(ct);
        if (!context.IsOk)
        {
            return Propagate<AddContext, SceneActionResult>(context);
        }

        return await push.AddSceneAsync(
            options.BaseUrl, options.ApiKey, stashId, title, monitored, searchForMovie: false,
            context.Value!.RootFolderPath, context.Value.QualityProfileId, context.Value.TagIds, ct);
    }

    // The version adapter for the connected instance, or null when the version is unmanageable. On v2 the
    // studio search-all path (episode search) GOes; the per-scene ADD paths still defer, gated on the
    // IWhisparrScenePush presence check BEFORE resolving the root + origin tag so a deferred v2 add stays
    // wire-free (v2 never holds IWhisparrScenePush).
    private IWhisparrAdapter? SelectAdapter()
        => AdapterSelector.SelectForVersion(options.SelectedVersion, client);

    // The catalogue role, or null when this instance's own API description does not declare the routes it
    // needs. Separate from SelectAdapter() because resolving capability costs a read: only the two per-entity
    // paths need it, and every other path keeps the version-only selection.
    private async Task<IWhisparrEntityCatalogue?> SelectCatalogueAsync(CancellationToken ct)
    {
        // The capability names a v3 route pair, so a non-v3 connection needs no document read at all — v2's
        // catalogue is its site walk, which is present by construction.
        if (!string.Equals(options.SelectedVersion, "v3", StringComparison.OrdinalIgnoreCase))
        {
            return AdapterSelector.SelectForVersion(options.SelectedVersion, client) as IWhisparrEntityCatalogue;
        }

        // Fail-closed here, unlike the read-only discovery gate: these two paths REACH for the role, so a
        // document that could not be read is treated exactly like one declaring no such route. Refusing on a
        // transient outage costs a user a retry; proceeding without the role would cost them a whole-set read
        // or, worse, a catalogue registered into the wrong answer.
        var read = await capability.ResolveAsync(options.BaseUrl, options.SelectedVersion!, ct);
        return AdapterSelector.SelectForVersion(
            options.SelectedVersion, client, WhisparrCapabilityPort.OrAbsent(read)) as IWhisparrEntityCatalogue;
    }

    // One entity's catalogue, addressed by the entity's OWN remote id resolved server-side. Both per-entity
    // callers read through this, so the rows a registering pass diffs against and the rows an importing pass
    // indexes can never come from different questions.
    private async Task<WhisparrResult<EntityCatalogue>> EntityCatalogueAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct)
    {
        if (await SelectCatalogueAsync(ct) is not { } catalogueSource)
        {
            // Fail-closed: an instance that does not offer the per-entity read is refused, never served by
            // widening back to the whole movie set.
            return WhisparrResult<EntityCatalogue>.Rejected(
                "This Whisparr build doesn't offer the per-entity scene list this needs.");
        }

        var remoteId = await EntityRemoteIdAsync(kind, coveEntityId, ct);
        return string.IsNullOrEmpty(remoteId)
            ? WhisparrResult<EntityCatalogue>.Rejected(
                $"Cove holds no Whisparr-usable id for this {EntityWord(kind)}, so its scenes can't be looked up.")
            : await catalogueSource.ListEntityMoviesAsync(options.BaseUrl, options.ApiKey, kind, remoteId, ct);
    }

    // The word a refusal names the entity by. Kept beside the refusals rather than derived at each one, so the
    // three sentences cannot drift into three vocabularies.
    private static string EntityWord(EntityKind kind) => kind switch
    {
        EntityKind.Studio => "studio",
        EntityKind.Performer => "performer",
        _ => "entity",
    };

    // The entity's own remote id in the connected version's family — StashDB on v3, ThePornDB on v2 — read
    // through the library port. A caller never supplies a remote id.
    private async Task<string?> EntityRemoteIdAsync(EntityKind kind, int coveEntityId, CancellationToken ct)
    {
        var identity = await library.LoadEntityIdentityAsync(kind, coveEntityId, ct);
        var ids = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase)
            ? identity?.TpdbIds
            : identity?.StashIds;
        return ids?.FirstOrDefault(id => !string.IsNullOrEmpty(id));
    }

    // The Whisparr identity of a STUDIO target — the entity's own StashDB id read through the library port, never
    // a caller-supplied one. A performer or tag target has no single parent studio, and a studio Cove holds no id
    // for is indistinguishable from one Whisparr does not track, so both answer null.
    private async Task<string?> ParentStudioForeignIdAsync(EntityKind kind, int coveEntityId, CancellationToken ct)
    {
        if (kind != EntityKind.Studio)
        {
            return null;
        }

        var identity = await library.LoadEntityIdentityAsync(kind, coveEntityId, ct);
        return identity?.StashIds.FirstOrDefault(id => !string.IsNullOrEmpty(id));
    }

    // Resolve the add prerequisites in one shot: the root path + the origin tag id set + the quality profile.
    // Every add path here (scene-add, scene-monitor, bulk-add-missing) registers a scene Whisparr does NOT own
    // yet, so there is no owned file to prefix-match — the file-less fallback root is the right derivation
    // (the owned file's real path is handled by the in-place adopt in ReflectOwnedAsync, not by an
    // add's root). Any resolve failing propagates verbatim, so a bad key / unreachable / no-root / no-profile
    // reaches the caller unchanged. <paramref name="parentStudioForeignId"/> is the scenes' parent studio where
    // the caller knows it.
    private async Task<WhisparrResult<AddContext>> ResolveAddContextAsync(
        CancellationToken ct, string? parentStudioForeignId = null)
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

        var profileResult = await _addContext.ResolveQualityProfileAsync(parentStudioForeignId, ct);
        if (!profileResult.IsOk)
        {
            return Propagate<int, AddContext>(profileResult);
        }

        return WhisparrResult<AddContext>.Ok(
            new AddContext(rootResult.Value!, profileResult.Value, tagResult.Value!));
    }

    // The resolved add prerequisites (root path + quality profile + origin tag id set) carried between resolve
    // and add.
    private sealed record AddContext(string RootFolderPath, int QualityProfileId, IReadOnlyList<int> TagIds);

    // The per-item tally every add fan-out here shares. Whisparr refuses an add whose studio the user put on
    // its import-exclusion list (WhisparrResultState.Excluded); that refusal IS the user's configured intent,
    // so counting it as a failure would report a deliberate choice as a fault and fill the failure ring with
    // scenes nothing can fix. It is counted as neither succeeded nor failed and named in the result Message.
    private sealed class AddTally
    {
        private int _succeeded;
        private int _failed;
        private int _skipped;

        internal void Record<T>(WhisparrResult<T> result)
        {
            if (result.IsOk)
            {
                _succeeded++;
            }
            else if (result.State == WhisparrResultState.Excluded)
            {
                _skipped++;
            }
            else
            {
                _failed++;
            }
        }

        internal BulkActionResult ToResult(int total)
        {
            var note = _skipped == 0
                ? null
                : $"{_skipped} {(_skipped == 1 ? "scene is" : "scenes are")} excluded in Whisparr, so {(_skipped == 1 ? "it was" : "they were")} skipped.";
            return new BulkActionResult(total, _succeeded, _failed, note);
        }
    }

    // Re-shape a non-Ok result of one payload type into the same state for the caller's return type.
    private static WhisparrResult<TTo> Propagate<TFrom, TTo>(WhisparrResult<TFrom> source)
        => WhisparrResult<TTo>.PropagateFrom(source);
}
