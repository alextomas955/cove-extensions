using System.Globalization;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Monitor;
using WhisparrSync.Push;
using WhisparrSync.SceneStatus;

namespace WhisparrSync.Adapters;

/// <summary>
/// The Whisparr v2 (Sonarr-based "v2" branch) adapter: implements the shared roles (aggregated by
/// <see cref="IWhisparrAdapter"/>) over v2's content model, and — deliberately — NONE of the v3-only roles,
/// so every v3-only verb structurally defers on v2 (no method to call). The connect/webhook transport is shared
/// with <see cref="V3Adapter"/> via <see cref="WhisparrAdapterBase"/>. The ONE substantive read is the site walk:
/// v2 has no <c>/movie</c> entity, so it walks <c>series → episode → episodefile</c> and synthesizes the
/// normalized scene rows the reused <c>IdentityMatcher</c> already understands. Two folds run over that one
/// walk — <see cref="ListMoviesAsync"/> collects the rows, <see cref="LoadStatusIndexAsync"/> keys them and keeps
/// none, and only the first of the two is reached by a shipped path here.
/// </summary>
internal sealed class V2Adapter(WhisparrClient client, TimeSpan? monitorSettleDelay = null)
    : WhisparrAdapterBase(client), IWhisparrAdapter, IWhisparrEntityCatalogue
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

    // A Cove studio maps to a v2 SITE (series); a performer has no v2 analog (v2 never implements
    // IWhisparrPerformerMonitor, so the performer path structurally has no method here). On v2 the stashId
    // parameter carries the TPDB site id, matched against the site's tvdbId slot — v2 rows carry no StashDB id.
    public Task<WhisparrResult<EntityMonitorResult>> SetStudioMonitorAsync(
        string baseUrl, string apiKey, string stashId, bool monitored, MonitorScope scope,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
        => SetSiteMonitorAsync(baseUrl, apiKey, stashId, monitored, scope, rootFolderPath, qualityProfileId, tagIds, ct);

    // A distinct site-add verb because SetSiteMonitorAsync only creates a site while turning monitor ON, leaving a
    // monitor-OFF caller with no add path. Shares the create spine (EnsureSiteAddedAsync); a present site short-
    // circuits to an idempotent no-op (no create/PUT). Loop-safety: the register add-body forces monitor and
    // monitorNewItems "none" + searchForMissingEpisodes false and this path issues no /command and no monitor
    // flip, so the unmonitored site can neither want nor grab an episode.
    public async Task<WhisparrResult<EntityMonitorResult>> RegisterStudioAsync(
        string baseUrl, string apiKey, string stashId,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var listResult = await Client.ListSeriesAsync(baseUrl, apiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrSeries[], EntityMonitorResult>(listResult);
        }

        var existing = FindByTpdb(listResult.Value!, stashId);
        if (existing is not null)
        {
            return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(Added: false, existing.Monitored));
        }

        var ensured = await EnsureSiteAddedAsync(
            baseUrl, apiKey, stashId,
            addable => BuildSiteRegisterBody(addable, stashId, rootFolderPath, qualityProfileId, tagIds), ct);
        return ensured.IsOk
            ? WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(ensured.Value!.Added, Monitored: false))
            : Propagate<SiteAddOutcome, EntityMonitorResult>(ensured);
    }

    public Task<WhisparrResult<EntityStatus>> GetStudioStatusAsync(
        string baseUrl, string apiKey, string stashId, CancellationToken ct)
        => GetSiteStatusAsync(baseUrl, apiKey, stashId, ct);

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
        var result = await Client.SendCommandAsync(baseUrl, apiKey, body, ct);
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
        var listResult = await Client.ListManualImportAsync(baseUrl, apiKey, folder, ct);
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

        var command = await Client.SendCommandAsync(baseUrl, apiKey, body, ct);
        if (!command.IsOk)
        {
            return Propagate<bool, bool>(command);
        }

        for (var attempt = 1; attempt <= ImportVerifyMaxAttempts; attempt++)
        {
            await Task.Delay(_monitorSettleDelay, ct);

            var episodesResult = await Client.ListEpisodesAsync(baseUrl, apiKey, seriesId, ct);
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
    // site is an empty array. Unlike the status count (which degrades a failed episode read to 0-of-0), a bulk
    // search MUST act on the real set, so a non-Ok episode read propagates rather than searching a partial set.
    public async Task<WhisparrResult<int[]>> ListStudioAttributedIdsAsync(
        string baseUrl, string apiKey, string stashId, bool monitoredOnly, CancellationToken ct)
    {
        var listResult = await Client.ListSeriesAsync(baseUrl, apiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrSeries[], int[]>(listResult);
        }

        var series = FindByTpdb(listResult.Value!, stashId);
        if (series is null)
        {
            return WhisparrResult<int[]>.Ok([]);
        }

        var episodesResult = await Client.ListEpisodesAsync(baseUrl, apiKey, series.Id, ct);
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

    // v2 serves the queue on the SAME /api/v3/queue path as v3 (Sonarr-shaped rows the projection normalizes).
    public Task<WhisparrResult<WhisparrQueuePage>> ListQueueAsync(string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct)
        => Client.ListQueueAsync(baseUrl, apiKey, page, pageSize, ct);

    // v2 serves wanted/missing on the SAME path; its episode-shaped rows bind their shared display fields
    // (id/title/monitored/hasFile/releaseDate/seriesId) into the movie shape, so no separate synthesis is needed.
    public Task<WhisparrResult<WhisparrMoviePage>> ListWantedAsync(string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct)
        => Client.ListWantedMissingAsync(baseUrl, apiKey, page, pageSize, ct);

    /// <summary>
    /// The v2 scene-enumeration remap (the reconciliation data source), MATERIALIZED: the site walk collected
    /// into one <see cref="WhisparrMovie"/> array. CRITICAL: <c>StashId</c> is null and <c>ItemType</c> is
    /// <c>"v2scene"</c> (never <c>"scene"</c>) so <c>IdentityMatcher.StashMatches</c> no-ops for v2 rows — a
    /// TPDB id is never compared to a Cove StashDB UUID.
    /// </summary>
    /// <remarks>
    /// The callers that need each scene's whole row keep this shape; a caller that only folds does not, and
    /// takes <see cref="LoadStatusIndexAsync"/> instead. Both run through the same walk, so neither the scene
    /// sequence nor the request sequence can differ between them.
    /// </remarks>
    public async Task<WhisparrResult<WhisparrMovie[]>> ListMoviesAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var folded = await FoldSiteWalkAsync(
            baseUrl, apiKey,
            new List<WhisparrMovie>(),
            (movies, scene) =>
            {
                movies.Add(scene);
                return movies;
            },
            ct);

        return folded.IsOk
            ? WhisparrResult<WhisparrMovie[]>.Ok([.. folded.Value!])
            : Propagate<List<WhisparrMovie>, WhisparrMovie[]>(folded);
    }

    /// <summary>
    /// The status index over the same synthesized scene set, folded per site so the whole set never exists at
    /// once.
    /// </summary>
    /// <remarks>
    /// NO SHIPPED PATH REACHES THIS on this generation, and that is deliberate: a synthesized row keys under
    /// nothing, so the index this builds is empty for a library of any size, and a consumer partitioning scenes by
    /// state over it reports every scene as one Whisparr does not have. This generation therefore does
    /// not declare <see cref="IWhisparrStatusIndexSource"/> and the summary refuses instead of answering. What
    /// survives here is the FOLD SHAPE: it is the form the index read would take if this generation's rows ever
    /// keyed (see that role's remarks for what deciding that costs), and it is measured because the property it
    /// holds — a per-site walk hands each site's scenes over and keeps one site at a time — is a property of the
    /// shared walk that <see cref="ListMoviesAsync"/>'s callers do reach.
    /// <para>
    /// The fold observer exists because the row sequence this fold is handed is otherwise unobservable on this
    /// generation: the index is empty however the fold behaves, so every count-level assertion is insensitive to a
    /// dropped or repeated scene in either direction. A null observer is not invoked.
    /// </para>
    /// </remarks>
    internal async Task<WhisparrResult<IReadOnlyDictionary<string, WhisparrMovieFacts>>> LoadStatusIndexAsync(
        string baseUrl, string apiKey, Action<WhisparrMovie>? onFolded, CancellationToken ct)
    {
        var folded = await FoldSiteWalkAsync(
            baseUrl, apiKey,
            new Dictionary<string, WhisparrMovieFacts>(StringComparer.OrdinalIgnoreCase),
            (index, scene) =>
            {
                SceneStatusProjector.IndexMovie(index, scene.Facts);

                // Last, so any filter that drops a scene returns ahead of this and the drop is observed. An
                // observer placed first would sit upstream of such a filter and see the undropped sequence.
                onFolded?.Invoke(scene);
                return index;
            },
            ct);

        return folded.IsOk
            ? WhisparrResult<IReadOnlyDictionary<string, WhisparrMovieFacts>>.Ok(folded.Value!)
            : WhisparrResult<IReadOnlyDictionary<string, WhisparrMovieFacts>>.PropagateFrom(folded);
    }

    // The ONE site walk both set reads run through, so the scene sequence and the request sequence cannot
    // diverge between them: GET /series once, then per site GET /episode + GET /episodefile, handing each
    // synthesized scene to the fold as it is produced. Fail-safe: any non-Ok read propagates verbatim and the
    // accumulator is discarded — a partial synthesis would report the sites that failed as scenes Whisparr
    // does not have, with full confidence.
    internal async Task<WhisparrResult<TAccumulator>> FoldSiteWalkAsync<TAccumulator>(
        string baseUrl, string apiKey, TAccumulator seed,
        Func<TAccumulator, WhisparrMovie, TAccumulator> fold, CancellationToken ct)
    {
        var seriesResult = await Client.ListSeriesAsync(baseUrl, apiKey, ct);
        if (!seriesResult.IsOk)
        {
            return Propagate<WhisparrSeries[], TAccumulator>(seriesResult);
        }

        var accumulator = seed;
        foreach (var series in seriesResult.Value!)
        {
            var episodesResult = await Client.ListEpisodesAsync(baseUrl, apiKey, series.Id, ct);
            if (!episodesResult.IsOk)
            {
                return Propagate<WhisparrEpisode[], TAccumulator>(episodesResult);
            }

            var filesResult = await Client.ListEpisodeFilesAsync(baseUrl, apiKey, series.Id, ct);
            if (!filesResult.IsOk)
            {
                return Propagate<WhisparrEpisodeFile[], TAccumulator>(filesResult);
            }

            foreach (var scene in SynthesizeEpisodes(series, episodesResult.Value!, filesResult.Value!, studioTitle: null))
            {
                accumulator = fold(accumulator, scene);
            }
        }

        return WhisparrResult<TAccumulator>.Ok(accumulator);
    }

    /// <summary>
    /// The read-only discovery catalogue for a Cove studio on v2: its v2 SITE (series) resolved by TPDB id, then
    /// that site's episodes projected into the SAME <see cref="WhisparrMovie"/> shape v3 produces so the missing
    /// list presents uniformly as "Scenes". A performer has NO v2 entity (performers are embedded
    /// <c>episode.actors</c> metadata), so a performer kind enumerates nothing with no wire call. A site Whisparr
    /// does not know is an empty set (a handled Ok, never an error). Issues no add/refresh/search command.
    /// </summary>
    public async Task<WhisparrResult<EntityCatalogue>> ListEntityMoviesAsync(
        string baseUrl, string apiKey, EntityKind kind, string remoteId, CancellationToken ct)
    {
        if (kind != EntityKind.Studio)
        {
            return WhisparrResult<EntityCatalogue>.Ok(EntityCatalogue.NotEnumerable);
        }

        var listResult = await Client.ListSeriesAsync(baseUrl, apiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrSeries[], EntityCatalogue>(listResult);
        }

        // The walk already knew whether the site resolved and was throwing that away as an empty set. It is the
        // same distinction the v3 existence read pays a request for, available here for free.
        var series = FindByTpdb(listResult.Value!, remoteId);
        if (series is null)
        {
            return WhisparrResult<EntityCatalogue>.Ok(EntityCatalogue.Unknown);
        }

        var episodesResult = await Client.ListEpisodesAsync(baseUrl, apiKey, series.Id, ct);
        if (!episodesResult.IsOk)
        {
            return Propagate<WhisparrEpisode[], EntityCatalogue>(episodesResult);
        }

        var filesResult = await Client.ListEpisodeFilesAsync(baseUrl, apiKey, series.Id, ct);
        if (!filesResult.IsOk)
        {
            return Propagate<WhisparrEpisodeFile[], EntityCatalogue>(filesResult);
        }

        // Stamp the site title so the entity display name resolves off the catalogue without a second lookup.
        var scenes = SynthesizeEpisodes(series, episodesResult.Value!, filesResult.Value!, series.Title).ToArray();
        return WhisparrResult<EntityCatalogue>.Ok(EntityCatalogue.Known(scenes));
    }

    // Synthesize one WhisparrMovie per episode of a site (the uniform v2 "scene" shape), joining
    // episode.episodeFileId -> episodefile.path (last row wins on a duplicate id; an absent file yields
    // MovieFile null). CRITICAL: StashId stays null and ItemType is "v2scene" (never "scene") so
    // IdentityMatcher.StashMatches no-ops — a v2 scene is keyed on its TPDB id in ForeignId. studioTitle stamps
    // the site name for the discovery entity display (reconciliation passes null).
    private static IEnumerable<WhisparrMovie> SynthesizeEpisodes(
        WhisparrSeries series, WhisparrEpisode[] episodes, WhisparrEpisodeFile[] files, string? studioTitle)
    {
        var pathByFileId = new Dictionary<int, string?>();
        foreach (var file in files)
        {
            pathByFileId[file.Id] = file.Path;
        }

        foreach (var episode in episodes)
        {
            WhisparrMovieFile? movieFile = null;
            if (episode.EpisodeFileId != 0 && pathByFileId.TryGetValue(episode.EpisodeFileId, out var path))
            {
                movieFile = new WhisparrMovieFile(episode.EpisodeFileId, path);
            }

            yield return new WhisparrMovie(
                Id: episode.Id,
                Title: episode.Title,
                Year: ParseYear(episode.ReleaseDate),
                StashId: null,
                ForeignId: episode.TvdbId?.ToString(CultureInfo.InvariantCulture),
                ItemType: "v2scene",
                Monitored: episode.Monitored,
                HasFile: episode.HasFile,
                MovieFile: movieFile,
                StudioTitle: studioTitle,
                // The enclosing series is the site the owned-import ManualImport targets (a v2 scene =
                // a Sonarr episode under this series); v3 movie rows leave this null.
                SeriesId: series.Id,
                ReleaseDate: episode.ReleaseDate);
        }
    }

    // Site (series) add-then-flip, mirroring V3Adapter.SetStudioMonitorAsync over v2's SITE model. GET /series
    // (matched by the TPDB id in tvdbId) answers whether the site is already added. Absent + ON: look the
    // addable row up (tpdb:{id}), add it monitored:false NON-grabbing, then PUT the requested state; a duplicate
    // add (400 SeriesExistsValidator = Conflict) re-reads the existing row (never a second create). Absent + OFF
    // is a no-op (nothing to unmonitor). v2 has no post-create refresh (the v3 RefreshStudios quirk), so the PUT
    // is authoritative — no verify loop.
    //
    // Decision — v2 gets NO Cove-owned refresh-on-monitor population step (unlike V3Adapter.PopulateCatalogueAsync):
    // the v3 flood that path fixes is Whisparr Eros's OWN post-monitor RefreshStudios leaving the discovered
    // back-catalogue HARD-CODED monitored regardless of the requested scope. Sonarr (v2) has no such quirk — it
    // HONORS the series add's addOptions.monitor lever, so NewReleases ("none") leaves the back-catalogue
    // unmonitored and AllScenes ("all") wants it, with monitorNewItems:"all" acquiring future episodes either way
    // (see BuildSiteAddBody). The scope intent is already correct at add time, so there is nothing to reconcile
    // and no analog to fix. Adding a speculative v2 refresh/episode-editor population step would be an UNVERIFIED
    // behavior change that risks arming a grab, so v2 stays on its existing add-body lever. Loop-safety
    // parity: this path issues NO /command (no RefreshSeries, no episode search) — asserted by V2OutwardParityTests.
    private async Task<WhisparrResult<EntityMonitorResult>> SetSiteMonitorAsync(
        string baseUrl, string apiKey, string tpdbId, bool monitored, MonitorScope scope,
        string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds, CancellationToken ct)
    {
        var listResult = await Client.ListSeriesAsync(baseUrl, apiKey, ct);
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

            var ensured = await EnsureSiteAddedAsync(
                baseUrl, apiKey, tpdbId,
                addable => BuildSiteAddBody(addable, tpdbId, scope, rootFolderPath, qualityProfileId, tagIds), ct);
            if (!ensured.IsOk)
            {
                return Propagate<SiteAddOutcome, EntityMonitorResult>(ensured);
            }

            existing = ensured.Value!.Series;
            added = ensured.Value.Added;
        }
        else if (!monitored && !existing.Monitored)
        {
            return WhisparrResult<EntityMonitorResult>.Ok(new EntityMonitorResult(added, Monitored: false));
        }

        var putResult = await Client.UpdateSeriesAsync(
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

    // The shared site-create spine both the monitor add-then-flip and the register verb call: look the addable
    // site up by TPDB, create it with the CALLER'S add-body (the monitor path and the register path differ ONLY
    // in that body's monitor/grab levers), and treat a duplicate (400 SeriesExistsValidator classified Conflict)
    // as success by re-reading the existing row — never a second POST. Returns the resolved row plus whether THIS
    // call created it. Issues no /command (loop-safety parity both callers rely on). `buildAddBody` is invoked
    // with the resolved lookup row because the v2 add-body needs its tvdbId/title/titleSlug identity.
    private async Task<WhisparrResult<SiteAddOutcome>> EnsureSiteAddedAsync(
        string baseUrl, string apiKey, string tpdbId, Func<WhisparrSeries, string> buildAddBody, CancellationToken ct)
    {
        var lookupResult = await Client.LookupSeriesAsync(baseUrl, apiKey, $"tpdb:{tpdbId}", ct);
        if (!lookupResult.IsOk)
        {
            return Propagate<WhisparrSeries[], SiteAddOutcome>(lookupResult);
        }

        var addable = FindByTpdb(lookupResult.Value!, tpdbId);
        if (addable is null)
        {
            return WhisparrResult<SiteAddOutcome>.Unreachable($"no v2 site matches tpdb:{tpdbId}");
        }

        var createResult = await Client.CreateSeriesAsync(baseUrl, apiKey, buildAddBody(addable), ct);

        if (createResult.State == WhisparrResultState.Conflict)
        {
            var reread = await Client.ListSeriesAsync(baseUrl, apiKey, ct);
            if (!reread.IsOk)
            {
                return Propagate<WhisparrSeries[], SiteAddOutcome>(reread);
            }

            var existing = FindByTpdb(reread.Value!, tpdbId);
            return existing is null
                ? WhisparrResult<SiteAddOutcome>.Unreachable("series create conflicted but no row on re-read")
                : WhisparrResult<SiteAddOutcome>.Ok(new SiteAddOutcome(existing, Added: false));
        }

        return createResult.IsOk
            ? WhisparrResult<SiteAddOutcome>.Ok(new SiteAddOutcome(createResult.Value!, Added: true))
            : Propagate<WhisparrSeries, SiteAddOutcome>(createResult);
    }

    // The resolved site plus whether THIS EnsureSiteAddedAsync call created it (false when it re-read a duplicate).
    private sealed record SiteAddOutcome(WhisparrSeries Series, bool Added);

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

            var reread = await Client.ListSeriesAsync(baseUrl, apiKey, ct);
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
                var reput = await Client.UpdateSeriesAsync(
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

        var episodesResult = await Client.ListEpisodesAsync(baseUrl, apiKey, seriesId, ct);
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
        return await Client.MonitorEpisodesAsync(baseUrl, apiKey, body, ct);
    }

    // Site status: resolve the site by its TPDB id, then count grabbed (hasFile) of total from its episodes.
    // An absent site is added:false / 0-of-0; a failed episode read degrades to 0-of-0 (never a misleading count).
    private async Task<WhisparrResult<EntityStatus>> GetSiteStatusAsync(
        string baseUrl, string apiKey, string tpdbId, CancellationToken ct)
    {
        var listResult = await Client.ListSeriesAsync(baseUrl, apiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrSeries[], EntityStatus>(listResult);
        }

        var series = FindByTpdb(listResult.Value!, tpdbId);
        if (series is null)
        {
            return WhisparrResult<EntityStatus>.Ok(new EntityStatus(Added: false, Monitored: false, ScenesPresent: 0, ScenesTotal: 0));
        }

        var episodesResult = await Client.ListEpisodesAsync(baseUrl, apiKey, series.Id, ct);
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
            tags = tagIds,
            addOptions = new
            {
                monitor = scope == MonitorScope.AllScenes ? "all" : "none",
                searchForMissingEpisodes = false,
                searchForCutoffUnmetEpisodes = false,
            },
        });

    // The v2 SITE REGISTER body: BuildSiteAddBody with EVERY monitor/grab lever forced OFF (loop-safety). Unlike
    // the monitor add-body's scope-driven monitor:"all"/"none", here monitored:false + addOptions.monitor:"none" +
    // monitorNewItems:"none" leave the site, its back-catalogue, and future episodes all unmonitored, and
    // searchForMissingEpisodes/searchForCutoffUnmetEpisodes false keep the add from grabbing — nothing is armed.
    // The origin tags keep the registered site attributable (cove-sync) and the create idempotent.
    private static string BuildSiteRegisterBody(
        WhisparrSeries addable, string tpdbId, string rootFolderPath, int qualityProfileId, IReadOnlyList<int> tagIds)
        => JsonSerializer.Serialize(new
        {
            tvdbId = addable.TvdbId ?? ParseTpdb(tpdbId),
            title = addable.Title,
            titleSlug = addable.TitleSlug,
            qualityProfileId,
            rootFolderPath,
            monitored = false,
            monitorNewItems = "none",
            tags = tagIds,
            addOptions = new
            {
                monitor = "none",
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
}
