using System.Text;
using System.Text.Json;
using WhisparrSync.Client;

namespace WhisparrSync.Discovery;

/// <summary>
/// Transport-only, hand-rolled GraphQL client for a StashDB (stash-box) instance — the opt-in direct-metadata
/// source that enumerates an UNMONITORED studio's full scene catalogue, which Whisparr cannot do for an
/// unmonitored entity. It attaches the <c>ApiKey</c> header (NOT <c>X-Api-Key</c> — that is Whisparr's), applies a
/// per-call timeout, guards status + <c>Content-Type</c> before deserializing, and classifies every outcome into
/// the shared <see cref="WhisparrResult{T}"/> instead of throwing — mirroring <see cref="WhisparrClient"/>'s
/// spine. Every operation it issues is a GraphQL QUERY — the catalogue read, the tag-name resolver, and the two
/// whole-set aggregates: no mutation, no grab.
/// </summary>
internal sealed class StashDbGraphQlClient(HttpClient http)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    // StashDB honors per_page up to 100. MaxPages is a hard ceiling bounding the loop against a
    // runaway count / a server that never advances it.
    private const int PerPage = 100;
    private const int MaxPages = 60;

    // StashDB caps requests per account at "low tens/minute" (exact limit unpublished) — a 429 gets
    // a few conservative back-off retries before the read gives up.
    private const int Max429Retries = 3;

    // The single read-only operation this client issues. The selection is deliberately minimal — the ids +
    // display facts the Missing-tab diff needs and nothing more. It is a query, never a mutation.
    // The performer and tag ids are what let a facet option derived from a rendered page still narrow the whole
    // catalogue when it is selected. Their measured cost at per_page=40 is 129,751 -> 174,785 bytes (+35%) and
    // 830 -> 902 ms (+9%), on the one request the page already costs.
    private const string QueryScenesQuery =
        "query($input: SceneQueryInput!) { queryScenes(input: $input) { count scenes { id title release_date details studio { id name } images { url } performers { performer { id name images { url } } } tags { id name } } } }";

    /// <summary>
    /// Reads a studio's full StashDB scene catalogue by its StashDB studio ids via <c>queryScenes</c>
    /// (<c>studios INCLUDES [ids]</c>), paginating at <see cref="PerPage"/> until <c>count</c> is covered.
    /// A parent studio passes its own id plus every child sub-studio's id, so the INCLUDES set unions the whole
    /// network in ONE read; a lone id behaves exactly as a single-studio read. Read-only: no mutating GraphQL,
    /// no Whisparr call. Returns the flattened scene set on <see cref="WhisparrResultState.Ok"/>; a bad key /
    /// unreachable / non-JSON body / rate-limit exhaustion propagates as the matching classified state.
    /// </summary>
    internal Task<WhisparrResult<StashDbCatalogue>> QueryScenesByStudioAsync(
        string endpoint, string apiKey, IReadOnlyList<string> studioIds, CancellationToken ct, int? maxRequestsPerMinute = null)
        => QueryScenesAsync(
            endpoint, apiKey,
            page => new StashDbSceneQueryInput(
                Page: page, PerPage: PerPage, Sort: "DATE", Direction: "DESC",
                Studios: new StashDbIdCriterion([.. studioIds], "INCLUDES")),
            ct, maxRequestsPerMinute);

    /// <summary>
    /// Reads a performer's full StashDB scene catalogue by its StashDB performer ids via <c>queryScenes</c>
    /// (<c>performers INCLUDES</c>) — the performer analogue of <see cref="QueryScenesByStudioAsync"/>, differing
    /// ONLY in which filter criterion the input carries. Same pagination, read-only, and classify-not-throw
    /// contract; the rows map into <c>WhisparrMovie</c> exactly as the studio path.
    /// </summary>
    internal Task<WhisparrResult<StashDbCatalogue>> QueryScenesByPerformerAsync(
        string endpoint, string apiKey, IReadOnlyList<string> performerIds, CancellationToken ct, int? maxRequestsPerMinute = null)
        => QueryScenesAsync(
            endpoint, apiKey,
            page => new StashDbSceneQueryInput(
                Page: page, PerPage: PerPage, Sort: "DATE", Direction: "DESC",
                Performers: new StashDbIdCriterion([.. performerIds], "INCLUDES")),
            ct, maxRequestsPerMinute);

    /// <summary>
    /// Reads a tag's full StashDB scene catalogue by its StashDB tag ids via <c>queryScenes</c>
    /// (<c>tags INCLUDES</c>) — the tag analogue of <see cref="QueryScenesByStudioAsync"/>, differing ONLY in
    /// which filter criterion the input carries. Same pagination, read-only, and classify-not-throw contract; the
    /// rows map into <c>WhisparrMovie</c> exactly as the studio path.
    /// </summary>
    internal Task<WhisparrResult<StashDbCatalogue>> QueryScenesByTagAsync(
        string endpoint, string apiKey, IReadOnlyList<string> tagIds, CancellationToken ct, int? maxRequestsPerMinute = null)
        => QueryScenesAsync(
            endpoint, apiKey,
            page => new StashDbSceneQueryInput(
                Page: page, PerPage: PerPage, Sort: "DATE", Direction: "DESC",
                Tags: new StashDbIdCriterion([.. tagIds], "INCLUDES")),
            ct, maxRequestsPerMinute);

    /// <summary>
    /// Reads ONE page of a studio's StashDB scene catalogue (<c>studios INCLUDES</c>) at the 1-based
    /// <paramref name="page"/> / <paramref name="perPage"/>, returning that page's rows plus the advertised
    /// catalogue count and whether a further page remains. Unlike <see cref="QueryScenesByStudioAsync"/> it does
    /// NOT loop to completion — the caller drives pagination. Same ApiKey header, 429 back-off, content-type
    /// guard, and classify-not-throw contract as the full read.
    /// </summary>
    internal Task<WhisparrResult<StashDbScenePage>> QueryScenesPageByStudioAsync(
        string endpoint, string apiKey, IReadOnlyList<string> studioIds, int page, int perPage,
        DiscoveryQuery query, CancellationToken ct)
        => QueryScenesPageAsync(
            endpoint, apiKey,
            PagedInput(
                page, perPage, query,
                studios: EntityCriterion(studioIds, query.StudioFilterId),
                performers: FilterCriterion(query.PerformerFilterId),
                tags: FilterCriterion(query.TagFilterId)),
            ct);

    /// <summary>
    /// Reads ONE page of a performer's StashDB scene catalogue (<c>performers INCLUDES</c>) — the performer
    /// analogue of <see cref="QueryScenesPageByStudioAsync"/>, differing only in which axis the entity occupies.
    /// </summary>
    internal Task<WhisparrResult<StashDbScenePage>> QueryScenesPageByPerformerAsync(
        string endpoint, string apiKey, IReadOnlyList<string> performerIds, int page, int perPage,
        DiscoveryQuery query, CancellationToken ct)
        => QueryScenesPageAsync(
            endpoint, apiKey,
            PagedInput(
                page, perPage, query,
                studios: FilterCriterion(query.StudioFilterId),
                performers: EntityCriterion(performerIds, query.PerformerFilterId),
                tags: FilterCriterion(query.TagFilterId)),
            ct);

    /// <summary>
    /// Reads ONE page of a tag's StashDB scene catalogue (<c>tags INCLUDES</c>) — the tag analogue of
    /// <see cref="QueryScenesPageByStudioAsync"/>, differing only in which axis the entity occupies.
    /// </summary>
    internal Task<WhisparrResult<StashDbScenePage>> QueryScenesPageByTagAsync(
        string endpoint, string apiKey, IReadOnlyList<string> tagIds, int page, int perPage,
        DiscoveryQuery query, CancellationToken ct)
        => QueryScenesPageAsync(
            endpoint, apiKey,
            PagedInput(
                page, perPage, query,
                studios: FilterCriterion(query.StudioFilterId),
                performers: FilterCriterion(query.PerformerFilterId),
                tags: EntityCriterion(tagIds, query.TagFilterId)),
            ct);

    // The one input all three paged reads compose, differing only in which axis carries the entity: the entity's
    // own criterion, the query's filter ids as additional criteria on the other axes, the year bound, and the
    // ordering — all in a single queryScenes request, since the criteria AND together upstream.
    private static StashDbSceneQueryInput PagedInput(
        int page, int perPage, DiscoveryQuery query,
        StashDbIdCriterion? studios, StashDbIdCriterion? performers, StashDbIdCriterion? tags)
    {
        var (sort, direction) = SortFor(query.Sort);
        return new StashDbSceneQueryInput(
            Page: page, PerPage: perPage, Sort: sort, Direction: direction,
            Studios: studios, Performers: performers, Tags: tags,
            Date: DateCriterionFor(query.Year, query.Sort));
    }

    // The entity's own criterion, present on every catalogue read: a caller-supplied filter id is only ever an
    // ADDITIONAL criterion, which bounds the result to a subset of this entity's own catalogue.
    // On the entity's OWN axis a query id replaces the entity's multi-id union only when it is a MEMBER of that
    // union — narrowing a parent studio's network to one of its children, which the user asked for and which
    // stays inside the parent's catalogue. A non-member id is dropped and the union stands, keeping a crafted id
    // off the wire. The resolved member is sent (never the caller's own string), since a hex UUID differing only
    // in case names the same id.
    private static StashDbIdCriterion EntityCriterion(IReadOnlyList<string> remoteIds, string? sameAxisFilterId)
    {
        var member = sameAxisFilterId is null
            ? null
            : remoteIds.FirstOrDefault(id => string.Equals(id, sameAxisFilterId, StringComparison.OrdinalIgnoreCase));
        return new StashDbIdCriterion(member is null ? [.. remoteIds] : [member], "INCLUDES");
    }

    private static StashDbIdCriterion? FilterCriterion(string? filterId)
        => filterId is null ? null : new StashDbIdCriterion([filterId], "INCLUDES");

    // DateCriterionInput carries exactly one value and one modifier: a closed year window is unsendable, and a
    // two-bound attempt would silently keep one half. EQUALS is not the way round it either: EQUALS on "2021" and
    // on "2021-01-01" both returned count: 0 with no error (verified live). What travels is ONE half-open bound
    // whose direction follows the requested ordering, which makes the wanted year a contiguous prefix of the
    // result; a title ordering has no such contiguity to exploit and takes the same lower bound.
    // The bound over-returns by construction, and it is not even strict at the boundary: date < 2016-01-01
    // returned rows dated "2016" and "2016-01" (verified live), because a StashDB release date is not always a
    // full ISO date. StashDbDiscoverySource trims the page by leading year.
    private static StashDbDateCriterion? DateCriterionFor(int? year, DiscoverySortMode sort)
        => year is not { } wanted
            ? null
            : sort == DiscoverySortMode.Newest
                ? new StashDbDateCriterion(
                    FormattableString.Invariant($"{wanted + 1:D4}-01-01"), "LESS_THAN")
                : new StashDbDateCriterion(
                    FormattableString.Invariant($"{wanted - 1:D4}-12-31"), "GREATER_THAN");

    // The StashDB sort/direction pair for an ordering. Only the two set-PRESERVING axes are reachable: the
    // provider's ranked feeds return a different-sized set for the same input, which an ordering must not do.
    private static (string Sort, string Direction) SortFor(DiscoverySortMode mode)
        => mode switch
        {
            DiscoverySortMode.Oldest => ("DATE", "ASC"),
            DiscoverySortMode.Title => ("TITLE", "ASC"),
            _ => ("DATE", "DESC"),
        };

    // A SINGLE page of queryScenes — no loop to the advertised count. hasMore treats a full page under the total
    // as "more remains" and a short page as the last one (a stale/estimated count must not force an extra empty
    // round-trip).
    private async Task<WhisparrResult<StashDbScenePage>> QueryScenesPageAsync(
        string endpoint, string apiKey, StashDbSceneQueryInput input, CancellationToken ct)
    {
        var pageResult = await FetchPageAsync(endpoint, apiKey, input, ct);
        if (!pageResult.IsOk)
        {
            return WhisparrResult<StashDbScenePage>.PropagateFrom(pageResult);
        }

        var result = pageResult.Value!.Data?.QueryScenes;
        if (result is null)
        {
            // A 200 with null data is a GraphQL error-only response, not an empty page — classify it so a source
            // error never masquerades as an empty catalogue (mirroring the full-loop read).
            return WhisparrResult<StashDbScenePage>.NotWhisparr();
        }

        var scenes = result.Scenes ?? [];
        var hasMore = scenes.Length >= input.PerPage && (long)input.Page * input.PerPage < result.Count;
        return WhisparrResult<StashDbScenePage>.Ok(new StashDbScenePage(scenes, result.Count, hasMore));
    }

    // The shared pagination spine both entity kinds use: the caller supplies the per-page input (studio- or
    // performer-filtered), the loop terminates on the advertised count OR a short page, and any non-Ok page
    // propagates verbatim. The only thing that varies between the studio and performer reads is the criterion.
    // Its callers keep one fixed ordering: this read feeds the action re-derive, whose result is a SET membership
    // question, and the order rows arrive in decides nothing there.
    // maxRequestsPerMinute (the host's configured rate limit, threaded per-call — never client instance state so
    // the DI singleton stays stateless) spaces successive pages; null keeps the prior unthrottled paging.
    private async Task<WhisparrResult<StashDbCatalogue>> QueryScenesAsync(
        string endpoint, string apiKey, Func<int, StashDbSceneQueryInput> inputForPage, CancellationToken ct,
        int? maxRequestsPerMinute = null)
    {
        var all = new List<StashDbScene>();
        var count = int.MaxValue;
        var pageSpacing = maxRequestsPerMinute is int rpm and > 0
            ? TimeSpan.FromMilliseconds(60_000.0 / rpm)
            : TimeSpan.Zero;
        var pagesRead = 0;
        for (var page = 1; (page - 1) * PerPage < count && page <= MaxPages; page++)
        {
            pagesRead = page;
            // Space pages by the configured rate (never before the first page); the per-page 429 back-off in
            // FetchPageAsync is independent and untouched.
            if (page > 1 && pageSpacing > TimeSpan.Zero)
            {
                await Task.Delay(pageSpacing, ct);
            }

            var pageResult = await FetchPageAsync(endpoint, apiKey, inputForPage(page), ct);
            if (!pageResult.IsOk)
            {
                return WhisparrResult<StashDbCatalogue>.PropagateFrom(pageResult);
            }

            var result = pageResult.Value!.Data?.QueryScenes;
            if (result is null)
            {
                // A 200 with null data is a GraphQL error-only response (e.g. an invalid query) — NOT an empty
                // catalogue; classifying it prevents a source error masquerading as "you own everything".
                return WhisparrResult<StashDbCatalogue>.NotWhisparr();
            }

            count = result.Count;
            if (result.Scenes is not { Length: > 0 } scenes)
            {
                break;
            }

            all.AddRange(scenes);

            // A page shorter than the requested size is the last page — stop even if the server's advertised
            // count disagrees (a stale/estimated count must not drive an extra empty round-trip).
            if (scenes.Length < PerPage)
            {
                break;
            }
        }

        // The loop can end three ways: the advertised count was covered, a short page proved the last page, or
        // MaxPages ran out. Only the third leaves rows unread, and it is indistinguishable from the other two in
        // the row array alone — which is how a capped catalogue used to be served as a complete one, silently
        // under-reporting missing scenes for exactly the largest studios. Report which happened.
        bool truncated = pagesRead >= MaxPages && pagesRead * PerPage < count;
        return WhisparrResult<StashDbCatalogue>.Ok(new StashDbCatalogue(all.ToArray(), truncated));
    }

    // findTagOrAlias resolves a tag by its name OR any alias, returning one tag or null — an exact resolver, so
    // there is no fuzzy-match ambiguity to arbitrate.
    private const string FindTagOrAliasQuery =
        "query($name: String!) { findTagOrAlias(name: $name) { id name } }";

    /// <summary>
    /// Resolves a tag NAME (or alias) to its StashDB id via <c>findTagOrAlias</c>, for a Cove tag that stores no
    /// StashDB remote id. Null when the source knows no such tag — the caller then reports its honest no-id state.
    /// </summary>
    internal async Task<WhisparrResult<string?>> ResolveTagIdByNameAsync(
        string endpoint, string apiKey, string tagName, CancellationToken ct)
    {
        var request = new StashDbTagNameRequest(FindTagOrAliasQuery, new StashDbTagNameVariables(tagName));
        var json = JsonSerializer.Serialize(request, StashDbJsonContext.Default.StashDbTagNameRequest);

        var result = await MetadataHttpSend.SendAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            ("ApiKey", apiKey),
            CallTimeout,
            Max429Retries,
            StashDbJsonContext.Default.StashDbTagLookupResponse,
            ct);

        if (!result.IsOk)
        {
            return WhisparrResult<string?>.PropagateFrom(result);
        }

        var id = result.Value?.Data?.FindTagOrAlias?.Id;
        return WhisparrResult<string?>.Ok(string.IsNullOrWhiteSpace(id) ? null : id);
    }

    // The whole-set aggregates. Neither is a catalogue read: each describes the ENTITY (a studio's performers, a
    // performer's studios), which is why one answer serves every page of that entity's catalogue.
    private const string QueryStudioPerformersQuery =
        "query($input: PerformerQueryInput!) { queryPerformers(input: $input) { count performers { id name } } }";

    private const string FindPerformerStudiosQuery =
        "query($id: ID!) { findPerformer(id: $id) { studios { studio { id name } } } }";

    // The roster arrives as ONE page: per_page is honoured exactly at this size and well past it (1000, 1500 and
    // 2000 each returned that many rows, live), which is what lets a single request stand in for a paged walk and
    // the offset instability that walk showed. Measured rosters: 385 and 565 arrive whole; 1696 does not, and the
    // returned count is what tells the caller the list stopped at A-M.
    private const int RosterPerPage = 1000;

    /// <summary>
    /// Reads every performer attributed to one studio's scenes — the studio page's performer option list over the
    /// WHOLE catalogue, as against the values one loaded page happens to carry.
    /// </summary>
    /// <remarks>
    /// One request, no loop, no page parameter: the roster is asked for as a single ordered page bounded by
    /// <see cref="RosterPerPage"/>, and the returned <see cref="StashDbPerformerRoster.Count"/> lets the caller
    /// see a roster that did not fit. Ordered by <c>NAME</c>/<c>ASC</c> for the reason
    /// <see cref="StashDbPerformerQueryInput"/> records.
    /// <para>
    /// The rows carry ids and names only. This shape also exposes each performer's <c>scene_count</c>, which is
    /// that performer's GLOBAL total and NOT their count within this studio. A per-option number read from it
    /// would mean something different from what a control implies, which is why the aggregate serves as a LIST.
    /// </para>
    /// A non-Ok result propagates its classified state; it is never flattened to an empty roster here, because an
    /// empty roster and an unreachable provider mean different things to the caller.
    /// </remarks>
    internal async Task<WhisparrResult<StashDbPerformerRoster>> QueryStudioPerformersAsync(
        string endpoint, string apiKey, string studioId, CancellationToken ct)
    {
        var request = new StashDbPerformerQueryRequest(
            QueryStudioPerformersQuery,
            new StashDbPerformerQueryVariables(
                new StashDbPerformerQueryInput(
                    StudioId: studioId, Page: 1, PerPage: RosterPerPage, Sort: "NAME", Direction: "ASC")));
        var json = JsonSerializer.Serialize(request, StashDbJsonContext.Default.StashDbPerformerQueryRequest);

        var result = await MetadataHttpSend.SendAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            ("ApiKey", apiKey),
            CallTimeout,
            Max429Retries,
            StashDbJsonContext.Default.StashDbPerformerQueryResponse,
            ct);

        if (!result.IsOk)
        {
            return WhisparrResult<StashDbPerformerRoster>.PropagateFrom(result);
        }

        // A 200 with null data is a GraphQL error-only response, not an empty roster. Classifying it keeps a
        // source error from reading as a studio with no performers.
        if (result.Value?.Data?.QueryPerformers is not { } roster)
        {
            return WhisparrResult<StashDbPerformerRoster>.NotWhisparr();
        }

        return WhisparrResult<StashDbPerformerRoster>.Ok(
            new StashDbPerformerRoster(roster.Performers ?? [], roster.Count));
    }

    /// <summary>
    /// Reads every studio a performer has scenes with — the performer page's studio option list over the whole
    /// catalogue. One request, and the field is unpaged, so there is nothing to bound or order.
    /// </summary>
    /// <remarks>
    /// A non-Ok result propagates its classified state; a performer the source does not know yields an empty list.
    /// </remarks>
    internal async Task<WhisparrResult<StashDbAggregateRow[]>> QueryPerformerStudiosAsync(
        string endpoint, string apiKey, string performerId, CancellationToken ct)
    {
        var request = new StashDbPerformerStudiosRequest(
            FindPerformerStudiosQuery, new StashDbPerformerIdVariables(performerId));
        var json = JsonSerializer.Serialize(request, StashDbJsonContext.Default.StashDbPerformerStudiosRequest);

        var result = await MetadataHttpSend.SendAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            ("ApiKey", apiKey),
            CallTimeout,
            Max429Retries,
            StashDbJsonContext.Default.StashDbPerformerStudiosResponse,
            ct);

        if (!result.IsOk)
        {
            return WhisparrResult<StashDbAggregateRow[]>.PropagateFrom(result);
        }

        if (result.Value?.Data is not { } data)
        {
            return WhisparrResult<StashDbAggregateRow[]>.NotWhisparr();
        }

        var entries = data.FindPerformer?.Studios ?? [];
        return WhisparrResult<StashDbAggregateRow[]>.Ok(
            [.. entries.Select(entry => entry.Studio).Where(studio => studio is not null).Select(studio => studio!)]);
    }

    private Task<WhisparrResult<StashDbGraphQlResponse>> FetchPageAsync(
        string endpoint, string apiKey, StashDbSceneQueryInput input, CancellationToken ct)
    {
        var request = new StashDbGraphQlRequest(QueryScenesQuery, new StashDbQueryVariables(input));
        var json = JsonSerializer.Serialize(request, StashDbJsonContext.Default.StashDbGraphQlRequest);

        return MetadataHttpSend.SendAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            // StashDB auth is the ApiKey header (per-account key), distinct from Whisparr's X-Api-Key.
            ("ApiKey", apiKey),
            CallTimeout,
            Max429Retries,
            StashDbJsonContext.Default.StashDbGraphQlResponse,
            ct);
    }
}
