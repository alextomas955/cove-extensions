using System.Collections.Frozen;
using WhisparrSync.Client;
using WhisparrSync.Contracts;

namespace WhisparrSync.Discovery;

/// <summary>
/// The direct-metadata discovery provider: enumerates an UNMONITORED studio's scene catalogue straight from
/// StashDB — the path Whisparr cannot serve for an unmonitored entity. It carries its OWN opt-in credential
/// (<see cref="NeedsCredential"/>) and can enumerate an entity Whisparr never tracked
/// (<see cref="CanEnumerateUnmonitored"/>). Each StashDB scene maps into a <see cref="WhisparrMovie"/> keyed on
/// the StashDB scene id, so the pure <see cref="DiscoveryService"/> diff runs UNCHANGED — provider-agnostic by
/// design.
/// </summary>
internal sealed class StashDbDiscoverySource(
    StashDbGraphQlClient client, string endpoint, string apiKey, int? maxRequestsPerMinute = null,
    DiscoveryAggregateMemo? aggregateMemo = null)
    : IDiscoverySource
{
    // Its own opt-in StashDB key, beyond the stored Whisparr connection.
    public bool NeedsCredential => true;

    // The whole point: it lists a studio Whisparr does not monitor (or does not know at all).
    public bool CanEnumerateUnmonitored => true;

    // All three orderings are native on queryScenes (verified live across four entities): DATE DESC, DATE ASC and
    // TITLE ASC each reorder the WHOLE catalogue, and each returns the same count the others do.
    private static readonly FrozenSet<DiscoverySortMode> NativeSorts = FrozenSet.ToFrozenSet(
        [DiscoverySortMode.Newest, DiscoverySortMode.Oldest, DiscoverySortMode.Title]);

    public IReadOnlySet<DiscoverySortMode> ServerSideSorts => NativeSorts;

    // All four facet axes narrow the WHOLE catalogue on queryScenes: studios, performers and tags are native
    // MultiIDCriterionInput criteria that AND with the entity's own, and the year is emulated from one date bound
    // plus a leading-year trim, since DateCriterionInput takes a single value and cannot express a closed window.
    private static readonly FrozenSet<DiscoveryFacetAxis> NativeFacets = FrozenSet.ToFrozenSet(
        [DiscoveryFacetAxis.Studio, DiscoveryFacetAxis.Performer, DiscoveryFacetAxis.Tag, DiscoveryFacetAxis.Year]);

    public IReadOnlySet<DiscoveryFacetAxis> ServerSideFacets => NativeFacets;

    private static readonly FrozenSet<DiscoveryFacetAxis> StudioPageAggregates =
        FrozenSet.ToFrozenSet([DiscoveryFacetAxis.Performer]);

    private static readonly FrozenSet<DiscoveryFacetAxis> PerformerPageAggregates =
        FrozenSet.ToFrozenSet([DiscoveryFacetAxis.Studio]);

    /// <summary>
    /// The axes a provider aggregate can enumerate the whole set for, per page kind: a studio's performer roster,
    /// and a performer's studio list.
    /// </summary>
    /// <remarks>
    /// A TAG page has no aggregate on ANY axis, and it is a permanent absence: the performer query input takes a
    /// studio id and offers no tag filter, and the studio query offers none either (both introspected). A tag
    /// page's option lists can only ever be the values its rendered rows carry.
    /// <para>
    /// The YEAR axis is page-derived on every kind. The two one-row date probes that exist would buy a RANGE and
    /// never an option list, and the year filter itself is exact wherever its options came from.
    /// </para>
    /// </remarks>
    public IReadOnlySet<DiscoveryFacetAxis> WholeSetFacetAxes(EntityKind kind)
        => kind switch
        {
            EntityKind.Studio => StudioPageAggregates,
            EntityKind.Performer => PerformerPageAggregates,
            // Named, not left to the default arm: a tag page's empty answer is a permanent provider fact.
            EntityKind.Tag => FrozenSet<DiscoveryFacetAxis>.Empty,
            _ => FrozenSet<DiscoveryFacetAxis>.Empty,
        };

    public async Task<WhisparrResult<DiscoveryCatalogue>> EnumerateCatalogueAsync(
        EntityKind kind, IReadOnlyList<string> remoteIds, CancellationToken ct)
    {
        // A studio reads queryScenes(studios INCLUDES [ids]); a performer performers INCLUDES; a tag tags INCLUDES.
        // The whole id set threads into one INCLUDES criterion, so a parent studio's parent+child ids union in a
        // single read (never one query per child). All kinds map into the same WhisparrMovie shape the pure diff
        // keys on, so the kind changes only the StashDB filter — nothing downstream.
        var scenesResult = kind switch
        {
            EntityKind.Studio => await client.QueryScenesByStudioAsync(endpoint, apiKey, remoteIds, ct, maxRequestsPerMinute),
            EntityKind.Performer => await client.QueryScenesByPerformerAsync(endpoint, apiKey, remoteIds, ct, maxRequestsPerMinute),
            EntityKind.Tag => await client.QueryScenesByTagAsync(endpoint, apiKey, remoteIds, ct, maxRequestsPerMinute),
            _ => WhisparrResult<StashDbCatalogue>.Ok(new StashDbCatalogue([], Truncated: false)),
        };

        if (!scenesResult.IsOk)
        {
            return WhisparrResult<DiscoveryCatalogue>.PropagateFrom(scenesResult);
        }

        var catalogue = scenesResult.Value!;
        return WhisparrResult<DiscoveryCatalogue>.Ok(
            new DiscoveryCatalogue([.. catalogue.Scenes.Select(MapToMovie)], catalogue.Truncated));
    }

    public async Task<WhisparrResult<DiscoveryPage>> EnumerateCataloguePageAsync(
        EntityKind kind, IReadOnlyList<string> remoteIds, int page, int perPage, DiscoveryQuery query,
        CancellationToken ct)
    {
        // ONE upstream page per call (never the full pagination loop): only the studio/performer/tag criterion
        // varies, the whole id set unions into one INCLUDES, and each StashDB scene maps into the same
        // WhisparrMovie shape the pure diff keys on. The query's ordering rides into the same single request, so
        // asking for a different ordering costs no extra call.
        var pageResult = kind switch
        {
            EntityKind.Studio => await client.QueryScenesPageByStudioAsync(endpoint, apiKey, remoteIds, page, perPage, query, ct),
            EntityKind.Performer => await client.QueryScenesPageByPerformerAsync(endpoint, apiKey, remoteIds, page, perPage, query, ct),
            EntityKind.Tag => await client.QueryScenesPageByTagAsync(endpoint, apiKey, remoteIds, page, perPage, query, ct),
            _ => WhisparrResult<StashDbScenePage>.Ok(new StashDbScenePage([], 0, false)),
        };

        if (!pageResult.IsOk)
        {
            return WhisparrResult<DiscoveryPage>.PropagateFrom(pageResult);
        }

        var scenePage = pageResult.Value!;
        var rows = TrimToYear(scenePage.Scenes, query.Year);
        var wholeSet = await WholeSetOptionsAsync(kind, remoteIds, ct);
        return WhisparrResult<DiscoveryPage>.Ok(
            new DiscoveryPage(
                [.. rows.Select(MapToMovie)], scenePage.Count, scenePage.HasMore,
                FacetOptions: Overlay(FacetOptionsFrom(rows), wholeSet),
                WholeSetAxes: AxesCovered(wholeSet)));
    }

    /// <summary>
    /// This entity's whole-set option lists, read through the caller's memo — once per entity open, never per page.
    /// </summary>
    /// <remarks>
    /// An axis is populated ONLY when its aggregate came back Ok, non-empty and complete, which is what lets every
    /// reader downstream take "present here" as "this list is the whole set". Null covers every other outcome.
    /// </remarks>
    private async Task<DiscoveryFacetOptions?> WholeSetOptionsAsync(
        EntityKind kind, IReadOnlyList<string> remoteIds, CancellationToken ct)
    {
        if (aggregateMemo is null || WholeSetFacetAxes(kind).Count == 0)
        {
            return null;
        }

        // Both aggregates take exactly ONE entity id, while a parent studio's catalogue read unions its whole
        // network into several. One child's roster would describe part of that read while looking like all of it.
        if (remoteIds is not [{ Length: > 0 } entityId])
        {
            return null;
        }

        var result = await aggregateMemo(kind, remoteIds, () => FetchWholeSetOptionsAsync(kind, entityId, ct), ct);
        return result.IsOk ? result.Value : null;
    }

    // The aggregate read behind the memo. A non-Ok provider result propagates (the memo caches only an Ok one, so
    // a transient failure is not sticky); an Ok result with no usable rows is an empty option set, cached, and the
    // page's own values then stand.
    private async Task<WhisparrResult<DiscoveryFacetOptions>> FetchWholeSetOptionsAsync(
        EntityKind kind, string entityId, CancellationToken ct)
    {
        if (kind == EntityKind.Performer)
        {
            var studios = await client.QueryPerformerStudiosAsync(endpoint, apiKey, entityId, ct);
            return studios.IsOk
                ? WhisparrResult<DiscoveryFacetOptions>.Ok(
                    new DiscoveryFacetOptions(
                        Studios: DiscoveryFacetOptionList.Build(studios.Value!.Select(row => (row.Id, row.Name)))))
                : WhisparrResult<DiscoveryFacetOptions>.PropagateFrom(studios);
        }

        var roster = await client.QueryStudioPerformersAsync(endpoint, apiKey, entityId, ct);
        if (!roster.IsOk)
        {
            return WhisparrResult<DiscoveryFacetOptions>.PropagateFrom(roster);
        }

        // A roster the provider holds more of than it returned is an alphabetical PREFIX of itself. Offering it
        // would give a list that neither covers the set nor matches the rendered rows — two different values for
        // one control, and the label could only be true of one of them. The page's own values stand.
        var value = roster.Value!;
        return WhisparrResult<DiscoveryFacetOptions>.Ok(
            value.Rows.Length >= value.Count
                ? new DiscoveryFacetOptions(
                    Performers: DiscoveryFacetOptionList.Build(value.Rows.Select(row => (row.Id, row.Name))))
                : new DiscoveryFacetOptions());
    }

    // The aggregate REPLACES the page's list on the axes it covers; every other axis keeps the page's own values.
    // An axis the aggregate did not fill leaves a working control alone. An empty dropdown helps nobody; a list
    // describing the rows on screen, labelled as one, does.
    private static DiscoveryFacetOptions Overlay(DiscoveryFacetOptions page, DiscoveryFacetOptions? wholeSet)
        => wholeSet is null
            ? page
            : page with
            {
                Studios = wholeSet.Studios ?? page.Studios,
                Performers = wholeSet.Performers ?? page.Performers,
            };

    // The axes whose served list is the whole set: exactly the ones the aggregate filled. Null when it filled
    // none, which is the same answer as never having asked.
    private static FrozenSet<DiscoveryFacetAxis>? AxesCovered(DiscoveryFacetOptions? wholeSet)
    {
        if (wholeSet is null)
        {
            return null;
        }

        List<DiscoveryFacetAxis> axes = [];
        if (wholeSet.Studios is { Length: > 0 })
        {
            axes.Add(DiscoveryFacetAxis.Studio);
        }

        if (wholeSet.Performers is { Length: > 0 })
        {
            axes.Add(DiscoveryFacetAxis.Performer);
        }

        return axes.Count == 0 ? null : FrozenSet.ToFrozenSet(axes);
    }

    /// <summary>
    /// The year axis's second half: the provider bound over-returns by construction, and this drops any row whose
    /// leading four digits are not the requested year.
    /// </summary>
    /// <remarks>
    /// <c>DateCriterionInput</c> takes a single value, and the bound is loose at the boundary because a StashDB
    /// release date is not always a full ISO date — <c>date &lt; 2016-01-01</c> returned rows dated <c>"2016"</c>
    /// and <c>"2016-01"</c> (verified live). The leading-four-digit rule matches what the client reads a bare
    /// <c>"1970"</c> as, keeping the two ends of the wire agreed on what a partial date means.
    /// <para>
    /// Only the ROWS are trimmed. The reported total stays the provider's own filtered catalogue count, which
    /// under this emulation is a slight OVER-estimate — the one place the year axis is approximate on this
    /// provider.
    /// </para>
    /// </remarks>
    private static StashDbScene[] TrimToYear(StashDbScene[] scenes, int? year)
        => year is { } wanted
            ? [.. scenes.Where(s => DiscoveryFacetOptionList.LeadingYear(s.ReleaseDate) == wanted)]
            : scenes;

    // The four axes' selectable values as the page itself carries them, each an {id,label} pair in the provider's
    // own id vocabulary — which is what lets an option read off a rendered page still narrow the whole catalogue
    // once selected. The ids ride the OPTION LIST and not the rows: the list holds one id per distinct value, and
    // the row DTOs the diff and the card read stay untouched.
    private static DiscoveryFacetOptions FacetOptionsFrom(StashDbScene[] scenes)
        => new(
            Studios: DiscoveryFacetOptionList.Build(scenes.Select(s => (s.Studio?.Id, s.Studio?.Name))),
            Performers: DiscoveryFacetOptionList.Build(scenes
                .SelectMany(s => s.Performers ?? [])
                .Select(p => (p.Performer?.Id, p.Performer?.Name))),
            Tags: DiscoveryFacetOptionList.Build(
                scenes.SelectMany(s => s.Tags ?? []).Select(t => (t.Id, t.Name))),
            Years: DiscoveryFacetOptionList.Years(scenes.Select(s => s.ReleaseDate)));

    /// <summary>
    /// Resolves a TAG name (or alias) to its StashDB id. Other kinds are addressed by a stored id.
    /// </summary>
    public Task<WhisparrResult<string?>> ResolveIdByNameAsync(EntityKind kind, string name, CancellationToken ct)
        => kind == EntityKind.Tag
            ? client.ResolveTagIdByNameAsync(endpoint, apiKey, name, ct)
            : Task.FromResult(WhisparrResult<string?>.Ok(null));

    // Maps a StashDB scene into the WhisparrMovie shape the diff keys on: the StashDB scene id in StashId (the
    // StashDb id family CandidateIds reads first), ItemType "scene", the display facts, and the studio name for
    // the entity-name stamp. A StashDB scene carries screenshots (no poster), so its first image maps to a
    // landscape "screenshot" cover — the wide card renders it, and the poster-kind lookup still finds none (the
    // poster fallback tile path is unchanged).
    private static WhisparrMovie MapToMovie(StashDbScene scene)
        => new(
            Id: 0,
            Title: scene.Title,
            Year: null,
            StashId: scene.Id,
            ForeignId: null,
            ItemType: "scene",
            Monitored: false,
            HasFile: false,
            MovieFile: null,
            StudioTitle: scene.Studio?.Name,
            ReleaseDate: scene.ReleaseDate,
            Images: CoverImages(scene),
            PerformerNames: PerformerNames(scene),
            TagNames: TagNames(scene),
            Overview: string.IsNullOrEmpty(scene.Details) ? null : scene.Details,
            PerformerImageUrls: PerformerImageUrls(scene));

    // The named performers on the scene (a non-owned scene has no Cove entity to link the chip to). The name and
    // image arrays are built from the SAME filtered list so they stay index-aligned; an empty image slot is ""
    // (the chip falls back to a placeholder avatar). Null (not empty) when the scene names none, so a downstream
    // renderer distinguishes absent from present-but-empty.
    private static StashDbPerformer[] NamedPerformers(StashDbScene scene)
        => scene.Performers?
            .Select(p => p.Performer)
            .Where(p => p is not null && !string.IsNullOrEmpty(p.Name))
            .Select(p => p!)
            .ToArray() ?? [];

    private static string[]? PerformerNames(StashDbScene scene)
    {
        var named = NamedPerformers(scene);
        return named.Length > 0 ? [.. named.Select(p => p.Name!)] : null;
    }

    private static string[]? PerformerImageUrls(StashDbScene scene)
    {
        var named = NamedPerformers(scene);
        return named.Length > 0
            ? [.. named.Select(p => p.Images?.FirstOrDefault(i => !string.IsNullOrEmpty(i.Url))?.Url ?? "")]
            : null;
    }

    private static string[]? TagNames(StashDbScene scene)
    {
        var names = scene.Tags?
            .Select(tag => tag.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();
        return names is { Length: > 0 } ? names : null;
    }

    // The first StashDB screenshot mapped to a landscape cover the card renders (its url is a source-served
    // absolute url, so it rides RemoteUrl). Null when the scene carries no image — the card renders a fallback tile.
    private static WhisparrImage[]? CoverImages(StashDbScene scene)
    {
        var url = scene.Images?.FirstOrDefault(i => !string.IsNullOrEmpty(i.Url))?.Url;
        return string.IsNullOrEmpty(url) ? null : [new WhisparrImage("screenshot", null, url)];
    }
}
