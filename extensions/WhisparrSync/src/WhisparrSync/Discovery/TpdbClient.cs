using System.Globalization;
using System.Text;
using WhisparrSync.Client;

namespace WhisparrSync.Discovery;

/// <summary>
/// Transport-only, hand-rolled REST client for ThePornDB — the opt-in direct-metadata source that enumerates an
/// UNMONITORED v2 SITE's full scene catalogue, which Whisparr cannot do for an unmonitored entity.
/// </summary>
/// <remarks>
/// ThePornDB's stash-box GraphQL <c>queryScenes</c> is gated (null <c>scenes</c> + a placeholder count for any
/// studio/text filter — verified live), so the catalogue is read from the REST API instead:
/// <c>GET {rest}/scenes?site_id=</c> with an <c>Authorization: Bearer</c> token — the SAME per-account token
/// Cove stores for the theporndb.net box, which authenticates both surfaces. It applies a per-call timeout,
/// guards status + <c>Content-Type</c> before deserializing, and classifies every outcome into
/// <see cref="WhisparrResult{T}"/> instead of throwing — mirroring <see cref="StashDbGraphQlClient"/>'s spine.
/// It issues only paginated read GETs: no mutation, no grab.
/// </remarks>
internal sealed class TpdbClient(HttpClient http)
{
    /// <summary>ThePornDB's REST API host — a fixed service endpoint, distinct from the stash-box GraphQL endpoint Cove stores.</summary>
    internal const string DefaultRestBaseUrl = "https://api.theporndb.net";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    // ThePornDB honors per_page up to 100. MaxPages is a hard ceiling bounding the loop against a runaway
    // last_page / a server that never advances the page.
    private const int PerPage = 100;
    private const int MaxPages = 60;

    // A 429 gets a few conservative back-off retries before the read gives up.
    private const int Max429Retries = 3;

    /// <summary>
    /// Reads a v2 site's full ThePornDB scene catalogue by its numeric ThePornDB site id via
    /// <c>GET /scenes?site_id=</c>, paginating until <c>meta.last_page</c>. Read-only: no mutating call, no
    /// Whisparr call. Returns the flattened scene set on <see cref="WhisparrResultState.Ok"/>; a bad token /
    /// unreachable / non-JSON body / rate-limit exhaustion propagates as the matching classified state, never a
    /// throw. <paramref name="maxRequestsPerMinute"/> (the host's configured rate limit, threaded per-call — never
    /// client instance state, so the DI singleton stays stateless) spaces successive pages; null keeps the prior
    /// unthrottled paging.
    /// </summary>
    internal Task<WhisparrResult<TpdbCatalogue>> QueryScenesBySiteAsync(
        string restBaseUrl, string token, string siteId, CancellationToken ct, int? maxRequestsPerMinute = null)
        => QueryAllPagesAsync(
            restBaseUrl, token, ScenesCriterion(SiteFragment(siteId), TpdbSceneFilter.None), ct, maxRequestsPerMinute);

    /// <summary>
    /// Reads a performer's full ThePornDB scene catalogue by the performer id Cove stores as its ThePornDB remote
    /// id, via the dedicated <c>GET /performers/{id}/scenes</c>. Same Bearer auth, page spacing, <c>MaxPages</c>
    /// ceiling, 429 back-off, and classify-not-throw contract as <see cref="QueryScenesBySiteAsync"/>.
    /// </summary>
    /// <remarks>
    /// The identifier must be the CANONICAL performer UUID (or slug), which is what Cove stores. A scene row's
    /// <c>performers[].id</c> is a per-scene APPEARANCE uuid and this path returns 404 for it (verified live);
    /// the appearance NUMERIC <c>performers[]._id</c> and the canonical numeric <c>parent._id</c> are not this
    /// path's currency either — the numeric one keys the <c>/scenes</c> collection route
    /// (<see cref="QueryScenesPageByPerformerCollectionAsync"/>) and nothing else.
    /// </remarks>
    internal Task<WhisparrResult<TpdbCatalogue>> QueryScenesByPerformerAsync(
        string restBaseUrl, string token, string performerId, CancellationToken ct, int? maxRequestsPerMinute = null)
        => QueryAllPagesAsync(
            restBaseUrl, token, PerformerSubResource(performerId), ct, maxRequestsPerMinute);

    /// <summary>
    /// Reads a tag's full ThePornDB scene catalogue by the NUMERIC ThePornDB tag id Cove stores as its remote id.
    /// Same auth, page spacing, <c>MaxPages</c> ceiling, 429 back-off, and classify-not-throw contract as the
    /// other reads.
    /// </summary>
    /// <remarks>
    /// A broad tag is a WINDOWED view, not an exhaustive one: this loop stops at <c>MaxPages</c> (6,000 scenes),
    /// under the source's own 10,000-row ceiling. The caller-driven
    /// <see cref="QueryScenesPageByTagAsync"/> is the primary surface for a large tag.
    /// </remarks>
    internal Task<WhisparrResult<TpdbCatalogue>> QueryScenesByTagAsync(
        string restBaseUrl, string token, string numericTagId, CancellationToken ct, int? maxRequestsPerMinute = null)
        => QueryAllPagesAsync(
            restBaseUrl, token, ScenesCriterion(TagFragment(numericTagId), TpdbSceneFilter.None), ct, maxRequestsPerMinute);

    /// <summary>
    /// Resolves a tag NAME to the numeric ThePornDB tag id its scene filter requires, via
    /// <c>GET /tags?q=</c>. Returns the id of the single case-insensitive EXACT name match, or null when the
    /// source knows the name under a different label — the caller then reports its honest no-id state.
    /// </summary>
    /// <remarks>
    /// <c>q=</c> is a FUZZY relevance search (<c>q=Anal</c> also returns "Anal Masturbation", "Anal Squirting"):
    /// the first row is not the answer, only an exact name match is. Several rows sharing the name count as
    /// unresolved. <c>?name=</c> is silently ignored by the source, leaving <c>q=</c> plus this exact-match filter
    /// as the only reliable lookup.
    /// </remarks>
    internal async Task<WhisparrResult<string?>> ResolveTagIdByNameAsync(
        string restBaseUrl, string token, string tagName, CancellationToken ct)
    {
        var url = $"{restBaseUrl.TrimEnd('/')}/tags?q={Uri.EscapeDataString(tagName)}&per_page={PerPage}&page=1";

        var result = await MetadataHttpSend.SendAsync(
            http,
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                return req;
            },
            ("Authorization", "Bearer " + token),
            CallTimeout,
            Max429Retries,
            TpdbJsonContext.Default.TpdbTagLookupResponse,
            ct);

        if (!result.IsOk)
        {
            return WhisparrResult<string?>.PropagateFrom(result);
        }

        var exact = (result.Value?.Data ?? [])
            .Where(t => t.Id is not null && string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return WhisparrResult<string?>.Ok(exact.Length == 1 ? exact[0].Id!.Value.ToString(CultureInfo.InvariantCulture) : null);
    }

    private async Task<WhisparrResult<TpdbCatalogue>> QueryAllPagesAsync(
        string restBaseUrl, string token, string criterion, CancellationToken ct, int? maxRequestsPerMinute = null)
    {
        var all = new List<TpdbScene>();
        var lastPage = 1;
        var pageSpacing = maxRequestsPerMinute is int rpm and > 0
            ? TimeSpan.FromMilliseconds(60_000.0 / rpm)
            : TimeSpan.Zero;

        var pagesRead = 0;
        for (var page = 1; page <= lastPage && page <= MaxPages; page++)
        {
            pagesRead = page;
            // Space pages by the configured rate (never before the first page); the per-page 429 back-off in
            // FetchPageAsync is independent and untouched.
            if (page > 1 && pageSpacing > TimeSpan.Zero)
            {
                await Task.Delay(pageSpacing, ct);
            }

            var pageResult = await FetchPageAsync(restBaseUrl, token, criterion, page, PerPage, ct);
            if (!pageResult.IsOk)
            {
                return WhisparrResult<TpdbCatalogue>.PropagateFrom(pageResult);
            }

            var body = pageResult.Value!;
            if (body.Data is { Length: > 0 } scenes)
            {
                all.AddRange(scenes);
            }

            // meta.last_page bounds the loop; a missing/odd meta stops after this page (never a runaway round-trip).
            lastPage = body.Meta?.LastPage ?? page;
        }

        // The ceiling is the only exit that leaves rows unread, and it is invisible in the row array — which is
        // how a windowed catalogue was served as an exhaustive one. Report it rather than raise the ceiling.
        bool truncated = pagesRead >= MaxPages && lastPage > MaxPages;
        return WhisparrResult<TpdbCatalogue>.Ok(new TpdbCatalogue([.. all], truncated));
    }

    /// <summary>
    /// Reads ONE page of a v2 site's ThePornDB scene catalogue at the 1-based <paramref name="page"/> /
    /// <paramref name="perPage"/> — the read-path counterpart of <see cref="QueryScenesBySiteAsync"/> the
    /// incremental Missing tab loads a page at a time. It does NOT loop: the caller drives pagination. A page
    /// shorter than <paramref name="perPage"/> is the end of the catalogue; when a full page returns, a present
    /// <c>meta.last_page</c> still bounds it so a full final page does not advertise a phantom next. Same Bearer
    /// auth, 429 back-off, content-type guard, and classify-not-throw contract as the full read.
    /// </summary>
    internal Task<WhisparrResult<TpdbScenePage>> QueryScenesPageBySiteAsync(
        string restBaseUrl, string token, string siteId, int page, int perPage, CancellationToken ct,
        TpdbSceneFilter? filter = null)
        => QueryOnePageAsync(
            restBaseUrl, token,
            // The site is the entity's own axis here: a query's site id names no second narrowing to apply.
            ScenesCriterion(SiteFragment(siteId), (filter ?? TpdbSceneFilter.None) with { SiteId = null }),
            page, perPage, ct);

    /// <summary>
    /// Reads ONE page of a performer's ThePornDB scene catalogue — the read-path counterpart of
    /// <see cref="QueryScenesByPerformerAsync"/> the incremental Missing tab loads a page at a time. It does NOT
    /// loop: the caller drives pagination. Same short-page end-of-catalogue rule and classify-not-throw contract
    /// as <see cref="QueryScenesPageBySiteAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<TpdbScenePage>> QueryScenesPageByPerformerAsync(
        string restBaseUrl, string token, string performerId, int page, int perPage, CancellationToken ct)
        => QueryOnePageAsync(restBaseUrl, token, PerformerSubResource(performerId), page, perPage, ct);

    /// <summary>
    /// Reads ONE page of a performer's catalogue through the <c>/scenes</c> COLLECTION, keyed on the performer's
    /// canonical NUMERIC id (<c>performers[].parent._id</c>) — the route a FILTERED performer read must take.
    /// </summary>
    /// <remarks>
    /// <see cref="QueryScenesPageByPerformerAsync"/>'s sub-resource ignores every query parameter (verified live:
    /// <c>year</c>, <c>tags[]</c>, <c>site_id</c>, <c>orderBy</c> and <c>q</c> each left the total unchanged at
    /// 1168, against a collection route that moved it to 737, 871 and 591). The id MUST be the canonical numeric
    /// one: the three sibling identifiers each return <c>total: 0</c>, which reads as owning every scene.
    /// </remarks>
    internal Task<WhisparrResult<TpdbScenePage>> QueryScenesPageByPerformerCollectionAsync(
        string restBaseUrl, string token, string numericPerformerId, int page, int perPage,
        TpdbSceneFilter filter, CancellationToken ct)
        => QueryOnePageAsync(
            restBaseUrl, token,
            ScenesCriterion(PerformerFragment(numericPerformerId), filter with { PerformerId = null }),
            page, perPage, ct);

    /// <summary>
    /// Reads ONE page of a tag's ThePornDB scene catalogue — the primary surface for a tag, whose catalogue can be
    /// far larger than a site's or a performer's. It does NOT loop: the caller drives pagination.
    /// </summary>
    internal Task<WhisparrResult<TpdbScenePage>> QueryScenesPageByTagAsync(
        string restBaseUrl, string token, string numericTagId, int page, int perPage, CancellationToken ct,
        TpdbSceneFilter? filter = null)
        => QueryOnePageAsync(
            restBaseUrl, token,
            ScenesCriterion(TagFragment(numericTagId), (filter ?? TpdbSceneFilter.None) with { TagId = null }),
            page, perPage, ct);

    private async Task<WhisparrResult<TpdbScenePage>> QueryOnePageAsync(
        string restBaseUrl, string token, string criterion, int page, int perPage, CancellationToken ct)
    {
        var pageResult = await FetchPageAsync(restBaseUrl, token, criterion, page, perPage, ct);
        if (!pageResult.IsOk)
        {
            return WhisparrResult<TpdbScenePage>.PropagateFrom(pageResult);
        }

        var body = pageResult.Value!;
        var scenes = body.Data ?? [];

        // A short page is the reliable end-of-catalogue signal (the advertised count is a placeholder for a gated
        // query — verified live). On a full page, meta.last_page (when present) still stops a full final page
        // from claiming a phantom next. last_page is a PAGE count and never a row count: for one tag it read
        // 3,334 at per_page=3 and 100 at per_page=100 over the same set, and on a filtered read 6/2/1 at
        // per_page 10/40/100 while the total held at 56 (verified live). It must never enter a size calculation.
        var hasMore = scenes.Length >= perPage && (body.Meta is not { } meta || page < meta.LastPage);
        return WhisparrResult<TpdbScenePage>.Ok(new TpdbScenePage(scenes, hasMore, body.Meta?.Total));
    }

    // ThePornDB has no per-tag or per-site scene sub-resource; both filter the /scenes collection. A tag and a
    // performer travel as a Laravel object-map param whose KEY is the numeric id. The map VALUE must be non-empty
    // but its content is ignored (verified live: a wrong value still returns only scenes carrying the keyed tag).
    // A fixed placeholder then avoids a name lookup, which could only introduce drift.
    private const string IgnoredMapFilterValue = "1";

    private static string SiteFragment(string siteId)
        => $"site_id={Uri.EscapeDataString(siteId)}";

    private static string TagFragment(string numericTagId)
        => $"tags%5B{Uri.EscapeDataString(numericTagId)}%5D={IgnoredMapFilterValue}";

    private static string PerformerFragment(string numericPerformerId)
        => $"performers%5B{Uri.EscapeDataString(numericPerformerId)}%5D={IgnoredMapFilterValue}";

    // A performer is the ONE entity ThePornDB gives a scene sub-resource that this client still uses: it ignores
    // every query parameter (verified live — year, tags[], site_id, orderBy and q each left the total unchanged
    // at 1168). It serves an unfiltered read only.
    private static string PerformerSubResource(string performerUuid)
        => $"performers/{Uri.EscapeDataString(performerUuid)}/scenes";

    /// <summary>
    /// Any combination of the four <c>/scenes</c> filter axes a discovery read can ask ThePornDB for.
    /// </summary>
    internal sealed record TpdbSceneFilter(
        string? SiteId = null, string? PerformerId = null, string? TagId = null, int? Year = null)
    {
        /// <summary>No narrowing — the entity's own catalogue, whole.</summary>
        internal static readonly TpdbSceneFilter None = new();

        /// <summary>Whether any axis is set. An unfiltered performer read can keep the sub-resource.</summary>
        internal bool IsEmpty => SiteId is null && PerformerId is null && TagId is null && Year is null;
    }

    // The composed /scenes criterion: the ENTITY's own fragment, always first and always present — it is what
    // bounds the read to this entity's catalogue — followed by whichever client-supplied axes survive the digits
    // guard. FetchPageAsync's shipped '?'-versus-'&' separator then appends per_page/page, so no further
    // machinery is needed.
    private static string ScenesCriterion(string entityFragment, TpdbSceneFilter filter)
    {
        var criterion = new StringBuilder("scenes?").Append(entityFragment);
        if (Digits(filter.SiteId) is { } siteId)
        {
            criterion.Append('&').Append(SiteFragment(siteId));
        }

        if (Digits(filter.PerformerId) is { } performerId)
        {
            criterion.Append('&').Append(PerformerFragment(performerId));
        }

        if (Digits(filter.TagId) is { } tagId)
        {
            criterion.Append('&').Append(TagFragment(tagId));
        }

        if (filter.Year is { } year)
        {
            criterion.Append("&year=").Append(year.ToString(CultureInfo.InvariantCulture));
        }

        return criterion.ToString();
    }

    // A filter id that is not digits-only is DROPPED and never interpolated. ThePornDB answers an unknown or
    // malformed parameter with a 200 and ignores it (verified live: eleven plausible parameters each left the
    // total unchanged), which means a malformed value fails QUIETLY upstream and the validation has to be ours.
    // Uri.EscapeDataString stays alongside on every fragment — the guard is the primary defence, the escape the
    // second.
    private static string? Digits(string? filterId)
        => filterId is not null && DiscoveryQueryGuard.IsTpdbFilterId(filterId) ? filterId : null;

    private Task<WhisparrResult<TpdbScenesResponse>> FetchPageAsync(
        string restBaseUrl, string token, string criterion, int page, int perPage, CancellationToken ct)
    {
        // ThePornDB's REST embeds performers, tags, image, and description on the scene row by default, so the
        // enriched facets return with no include=/fields= expansion param on the query.
        var separator = criterion.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{restBaseUrl.TrimEnd('/')}/{criterion}{separator}per_page={perPage}&page={page}";

        return MetadataHttpSend.SendAsync(
            http,
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                return req;
            },
            // ThePornDB REST auth is a per-account Bearer token — the same token Cove stores for the box.
            ("Authorization", "Bearer " + token),
            CallTimeout,
            Max429Retries,
            TpdbJsonContext.Default.TpdbScenesResponse,
            ct);
    }
}
