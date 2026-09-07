using System.Collections.Frozen;
using System.Globalization;
using WhisparrSync.Client;
using WhisparrSync.Contracts;

namespace WhisparrSync.Discovery;

/// <summary>
/// The direct-metadata discovery provider for ThePornDB (v2): enumerates an UNMONITORED entity's scene catalogue
/// straight from ThePornDB's REST API — the path Whisparr cannot serve for an unmonitored entity. The analogue of
/// <see cref="StashDbDiscoverySource"/> for the TPDB box — same <see cref="IDiscoverySource"/> contract, same
/// <see cref="WhisparrMovie"/> projection the pure <see cref="DiscoveryService"/> diff keys on — differing only in
/// the transport (REST, because TPDB's stash-box <c>queryScenes</c> is gated; verified live) and the id family
/// the diff subtracts on (TPDB, not StashDB). It carries its OWN credential (<see cref="NeedsCredential"/>) and
/// can enumerate an entity Whisparr never tracked (<see cref="CanEnumerateUnmonitored"/>).
/// </summary>
/// <remarks>
/// Studio (a v2 site), performer, and tag all enumerate. ThePornDB gives a site and a performer their own scene
/// sub-resource; a tag instead filters the scene collection by its numeric id. Any other kind enumerates nothing
/// without a call.
/// </remarks>
internal sealed class TpdbDiscoverySource(
    TpdbClient client, string restBaseUrl, string token, int? maxRequestsPerMinute = null)
    : IDiscoverySource
{
    public bool NeedsCredential => true;

    public bool CanEnumerateUnmonitored => true;

    /// <summary>
    /// ThePornDB has no ordering to ask for, permanently. <c>orderBy</c> is a recognised <c>/scenes</c> parameter
    /// with no valid value — 32 candidates across two entities each answered 422 naming the parameter — while
    /// <c>sort</c> and <c>order</c> return byte-identical rows and are silently ignored. The empty set is the
    /// honest declaration, and the caller words its sort control as an ordering of the rows it loaded.
    /// </summary>
    public IReadOnlySet<DiscoverySortMode> ServerSideSorts => FrozenSet<DiscoverySortMode>.Empty;

    // All four facet axes narrow the WHOLE catalogue on /scenes, each proven by a total that CHANGED against an
    // unfiltered baseline in the same run: site_id, tags[<id>], performers[<parent._id>] and year=<YYYY>. The
    // year is native and EXACT here, and a narrow enough one can bring a saturated total back under the ceiling
    // (one tag alone reported 10,000; the same tag with year=2019 reported 4,580).
    private static readonly FrozenSet<DiscoveryFacetAxis> NativeFacets = FrozenSet.ToFrozenSet(
        [DiscoveryFacetAxis.Studio, DiscoveryFacetAxis.Performer, DiscoveryFacetAxis.Tag, DiscoveryFacetAxis.Year]);

    public IReadOnlySet<DiscoveryFacetAxis> ServerSideFacets => NativeFacets;

    /// <summary>
    /// No axis, on any kind: ThePornDB exposes no aggregate over a catalogue's facet values.
    /// </summary>
    /// <remarks>
    /// The honest consequence is that every v2 option list comes from the loaded page and is worded as such. Per
    /// year counts are NOT probed to fake one — one request per candidate year over an unbounded historical span
    /// is absurd for a page load, and the year FILTER is exact whatever its option list was derived from.
    /// </remarks>
    public IReadOnlySet<DiscoveryFacetAxis> WholeSetFacetAxes(EntityKind kind) => FrozenSet<DiscoveryFacetAxis>.Empty;

    public async Task<WhisparrResult<DiscoveryCatalogue>> EnumerateCatalogueAsync(
        EntityKind kind, IReadOnlyList<string> remoteIds, CancellationToken ct)
    {
        // An unserved kind returns empty WITHOUT a call — never a fabricated catalogue.
        if (!Enumerates(kind))
        {
            return WhisparrResult<DiscoveryCatalogue>.Ok(new DiscoveryCatalogue([], Truncated: false));
        }

        // TPDB has no entity-hierarchy analog to union (no sub-studios): each kind reads its own id, the first in
        // the set.
        var scenesResult = kind switch
        {
            EntityKind.Performer =>
                await client.QueryScenesByPerformerAsync(restBaseUrl, token, remoteIds[0], ct, maxRequestsPerMinute),
            EntityKind.Tag =>
                await client.QueryScenesByTagAsync(restBaseUrl, token, remoteIds[0], ct, maxRequestsPerMinute),
            _ => await client.QueryScenesBySiteAsync(restBaseUrl, token, remoteIds[0], ct, maxRequestsPerMinute),
        };

        if (!scenesResult.IsOk)
        {
            return WhisparrResult<DiscoveryCatalogue>.PropagateFrom(scenesResult);
        }

        var catalogue = scenesResult.Value!;
        return WhisparrResult<DiscoveryCatalogue>.Ok(
            new DiscoveryCatalogue([.. catalogue.Scenes.Select(MapToMovie)], catalogue.Truncated));
    }

    // The query's four facet axes reach the provider; its ordering does not, which is exactly what the two
    // declarations above say. The entity's own axis is the read's subject, so only the OTHER axes narrow.
    public async Task<WhisparrResult<DiscoveryPage>> EnumerateCataloguePageAsync(
        EntityKind kind, IReadOnlyList<string> remoteIds, int page, int perPage, DiscoveryQuery query,
        CancellationToken ct)
    {
        if (!Enumerates(kind))
        {
            return WhisparrResult<DiscoveryPage>.Ok(new DiscoveryPage([], 0, false));
        }

        var filter = FilterFor(query);
        var pageResult = kind switch
        {
            EntityKind.Performer => await PerformerPageAsync(remoteIds[0], page, perPage, filter, ct),
            EntityKind.Tag =>
                await client.QueryScenesPageByTagAsync(restBaseUrl, token, remoteIds[0], page, perPage, ct, filter),
            _ => await client.QueryScenesPageBySiteAsync(restBaseUrl, token, remoteIds[0], page, perPage, ct, filter),
        };

        if (!pageResult.IsOk)
        {
            return WhisparrResult<DiscoveryPage>.PropagateFrom(pageResult);
        }

        var scenePage = pageResult.Value!;

        // The source's own count carries through: the tab reports the CATALOGUE size, not the one page fetched.
        // At the ceiling the count is a lower bound, flagged for the UI's "10,000+".
        // The flag is derived from the total THIS read returned and is never carried across queries: a filter can
        // drop a set below the ceiling and make the count exact again, and rendering "10,000+" over an
        // exactly-known 4,580 is the defect this project has already shipped once and reverted.
        return WhisparrResult<DiscoveryPage>.Ok(
            new DiscoveryPage(
                [.. scenePage.Scenes.Select(MapToMovie)],
                scenePage.Total,
                scenePage.HasMore,
                scenePage.Total >= TpdbReportedTotalCeiling,
                FacetOptionsFrom(scenePage.Scenes)));
    }

    // The v2 facet vocabulary maps onto ThePornDB's own: a Cove studio IS a ThePornDB site, and the studio
    // axis therefore carries the numeric site id.
    private static TpdbClient.TpdbSceneFilter FilterFor(DiscoveryQuery query)
        => new(query.StudioFilterId, query.PerformerFilterId, query.TagFilterId, query.Year);

    /// <summary>
    /// One page of a performer's catalogue: the sub-resource while nothing is filtered, the <c>/scenes</c>
    /// collection keyed on the canonical numeric id as soon as any axis is.
    /// </summary>
    /// <remarks>
    /// <c>/performers/{uuid}/scenes</c> ignores EVERY query parameter — <c>year</c>, <c>tags[]</c>,
    /// <c>site_id</c>, <c>orderBy</c> and <c>q</c> each left the total unchanged at 1168 against a collection
    /// route that moved it to 737, 871 and 591 (verified live). A filtered read cannot go through it.
    /// <para>
    /// The unfiltered read KEEPS it, deliberately. The sub-resource is cleanly date-descending while
    /// <c>/scenes</c> has no ordering contract at all (two sampled entities disagreed), and on the one generation
    /// that can never ask the provider to order, that incidental ordering is the only reason an unfiltered v2
    /// performer page 1 leads with recent scenes. Paging across two differently-ordered sources cannot arise,
    /// because the client resets to page 1 on any query change.
    /// </para>
    /// <para>
    /// Cove stores the canonical UUID and the collection route accepts only the canonical NUMERIC id, which is
    /// recoverable from a scene row whose <c>parent.id</c> equals the stored one. When it cannot be resolved the
    /// read falls back to the sub-resource: an unfiltered page of the RIGHT performer, whose axes the caller then
    /// narrows over the rows it loaded. Sending a filter without a resolvable entity key would either widen the
    /// read past this performer or return a zero indistinguishable from owning everything.
    /// </para>
    /// </remarks>
    private async Task<WhisparrResult<TpdbScenePage>> PerformerPageAsync(
        string performerUuid, int page, int perPage, TpdbClient.TpdbSceneFilter filter, CancellationToken ct)
    {
        var narrowed = filter with { PerformerId = null };
        if (narrowed.IsEmpty)
        {
            return await client.QueryScenesPageByPerformerAsync(restBaseUrl, token, performerUuid, page, perPage, ct);
        }

        var numericId = await ResolveNumericPerformerIdAsync(performerUuid, ct);
        if (!numericId.IsOk)
        {
            return WhisparrResult<TpdbScenePage>.PropagateFrom(numericId);
        }

        return numericId.Value is { } resolved
            ? await client.QueryScenesPageByPerformerCollectionAsync(
                restBaseUrl, token, resolved, page, perPage, narrowed, ct)
            : await client.QueryScenesPageByPerformerAsync(restBaseUrl, token, performerUuid, page, perPage, ct);
    }

    // ONE bounded probe: the smallest possible sub-resource page, read for the mapping from the stored UUID to
    // the canonical numeric id. Every scene the sub-resource returns lists this performer, so one row suffices,
    // and the row is matched on parent.id — the appearance ids on the same row name a different thing.
    private async Task<WhisparrResult<string?>> ResolveNumericPerformerIdAsync(
        string performerUuid, CancellationToken ct)
    {
        var probe = await client.QueryScenesPageByPerformerAsync(
            restBaseUrl, token, performerUuid, page: 1, perPage: 1, ct);
        if (!probe.IsOk)
        {
            return WhisparrResult<string?>.PropagateFrom(probe);
        }

        var numericId = probe.Value!.Scenes
            .SelectMany(scene => scene.Performers ?? [])
            .Where(p => string.Equals(p.Parent?.Uuid, performerUuid, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Parent!.NumericId)
            .FirstOrDefault(id => id is not null);

        return WhisparrResult<string?>.Ok(numericId?.ToString(CultureInfo.InvariantCulture));
    }

    // The four axes' selectable values as the page itself carries them, in ThePornDB's own id vocabulary — the
    // site's numeric id, the performer's canonical numeric id, the tag's numeric id, and the leading year. As on
    // the other provider the ids ride the OPTION LIST and not the rows, leaving MissingScene, MissingPerformer,
    // WhisparrMovie and DiscoveryService.Project untouched.
    private static DiscoveryFacetOptions FacetOptionsFrom(TpdbScene[] scenes)
        => new(
            Studios: DiscoveryFacetOptionList.Build(
                scenes.Select(s => (NumericId(s.Site?.Id), s.Site?.Name))),
            Performers: DiscoveryFacetOptionList.Build(scenes
                .SelectMany(s => s.Performers ?? [])
                .Select(p => (NumericId(p.Parent?.NumericId), p.Name))),
            Tags: DiscoveryFacetOptionList.Build(
                scenes.SelectMany(s => s.Tags ?? []).Select(t => (NumericId(t.Id), t.Name))),
            Years: DiscoveryFacetOptionList.Years(scenes.Select(s => s.Date)));

    private static string? NumericId(long? id)
        => id?.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolves a TAG name to ThePornDB's numeric tag id — the only id its scene filter accepts. Other kinds have
    /// no name lookup here: a site/performer is addressed by a stored id.
    /// </summary>
    public Task<WhisparrResult<string?>> ResolveIdByNameAsync(EntityKind kind, string name, CancellationToken ct)
        => kind == EntityKind.Tag
            ? client.ResolveTagIdByNameAsync(restBaseUrl, token, name, ct)
            : Task.FromResult(WhisparrResult<string?>.Ok(null));

    // ThePornDB stops counting at 10,000; any larger set reports exactly this value.
    private const int TpdbReportedTotalCeiling = 10_000;

    // An id-less entity never reaches here — the caller renders its honest no-source-id state instead.
    private static bool Enumerates(EntityKind kind)
        => kind is EntityKind.Studio or EntityKind.Performer or EntityKind.Tag;

    // The ThePornDB scene UUID lands in ForeignId — the only id family a v2 owned scene is subtracted by
    // (DiscoveryService keys the TPDB family on ForeignId), and the SAME id Cove stores for the scene. ItemType
    // "scene" presents uniformly with the v3 direct path. The rich facets mirror StashDbDiscoverySource's
    // projection — the card reads the same field set from either source.
    private static WhisparrMovie MapToMovie(TpdbScene scene)
        => new(
            Id: 0,
            Title: scene.Title,
            Year: null,
            StashId: null,
            ForeignId: scene.Id,
            ItemType: "scene",
            Monitored: false,
            HasFile: false,
            MovieFile: null,
            StudioTitle: scene.Site?.Name,
            ReleaseDate: scene.Date,
            Images: CoverImages(scene),
            PerformerNames: PerformerNames(scene),
            TagNames: TagNames(scene),
            Overview: string.IsNullOrWhiteSpace(scene.Description) ? null : scene.Description,
            PerformerImageUrls: PerformerImageUrls(scene));

    // The named performers on the scene. The name and image arrays are built from the SAME filtered list, keeping
    // them index-aligned; null (not empty) when the scene names none — a downstream renderer distinguishes absent
    // from present-but-empty.
    private static TpdbPerformer[] NamedPerformers(TpdbScene scene)
        => scene.Performers?
            .Where(p => p is not null && !string.IsNullOrEmpty(p.Name))
            .Select(p => p!)
            .ToArray() ?? [];

    private static string[]? PerformerNames(TpdbScene scene)
    {
        var named = NamedPerformers(scene);
        return named.Length > 0 ? [.. named.Select(p => p.Name!)] : null;
    }

    // The chip avatar: the 500×500 face preferred, then image, then thumbnail; an empty slot is "" to keep the
    // name and url arrays index-aligned (the chip falls back to a placeholder avatar).
    private static string[]? PerformerImageUrls(TpdbScene scene)
    {
        var named = NamedPerformers(scene);
        return named.Length > 0
            ? [.. named.Select(p => FirstNonEmpty(p.Face, p.Image, p.Thumbnail))]
            : null;
    }

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "";

    private static string[]? TagNames(TpdbScene scene)
    {
        var names = scene.Tags?
            .Select(tag => tag.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();
        return names is { Length: > 0 } ? names : null;
    }

    // The card cover, from ONE field: the poster. ThePornDB's landscape `image` points at the studio's own CDN,
    // which refuses cross-origin requests — measured 0 of 20 loading (404/403) while the TPDB-hosted `poster`
    // loaded 20 of 20 — so offering `image` only ever produced a broken request. Null when the scene carries no
    // poster; the card then shows its placeholder tile, as Cove's own card does.
    private static WhisparrImage[]? CoverImages(TpdbScene scene)
        => string.IsNullOrEmpty(scene.Poster)
            ? null
            : [new WhisparrImage("screenshot", null, scene.Poster)];
}
