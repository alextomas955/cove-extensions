using System.Collections.ObjectModel;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Discovery;
using WhisparrSync.SceneStatus;
using WhisparrSync.State;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The per-entity discovery slice's minimal-API surface: the missing-scene list for a studio/performer and the
/// count the host tab badge reads. Both are creds-reaching reads (they hit Whisparr with the stored key) and
/// grab nothing — the projection is the pure <see cref="Discovery.DiscoveryService"/> diff of the fetched
/// catalogue against the entity's owned Cove scenes.
/// </summary>
public sealed partial class WhisparrSync
{
    // The per-entity Missing-tab reads. /discovery/entity POSTs ONLY the Cove entity id + kind (never a remote
    // id — the server resolves it); /discovery/count is the same diff reduced to a count for the host tab
    // badge. Both are configure-gated + stored-creds-only: the body/query carries NO url/key.
    private const string DiscoveryEntityRoute = RouteBase + "/discovery/entity";
    private const string DiscoveryCountRoute = RouteBase + "/discovery/count";

    // Bounds one paged direct read to a grid-friendly slice rather than a whole catalogue.
    private const int DirectPageSize = 40;

    /// <summary>
    /// Registers the discovery slice's routes. The entity read POSTs the Cove id + kind; the count is a GET with
    /// <c>kind</c> + <c>entityId</c> query params (the host substitutes <c>{entityId}</c> into the tab's count
    /// endpoint template). Each lambda delegates to an extracted instance handler so it is unit-testable host-free.
    /// </summary>
    private void MapDiscoveryEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(DiscoveryEntityRoute,
            (DiscoveryEntityRequest req, WhisparrClient client, StashDbGraphQlClient stashDbClient,
                TpdbClient tpdbClient, CancellationToken ct)
                => DiscoveryEntityAsync(req, client, stashDbClient, tpdbClient, ct)).ConfigureGated();

        endpoints.MapGet(DiscoveryCountRoute,
            (string? kind, int? entityId, WhisparrClient client, StashDbGraphQlClient stashDbClient,
                TpdbClient tpdbClient, CancellationToken ct)
                => DiscoveryCountAsync(kind, entityId, client, stashDbClient, tpdbClient, ct)).ConfigureGated();
    }

    /// <summary>
    /// The missing-scene list for one studio/performer: the entity's direct metadata-source catalogue (StashDB on
    /// v3, ThePornDB on v2) minus the scenes Cove already owns minus the Whisparr exclusions. The remote id is
    /// resolved SERVER-SIDE from the Cove entity id (never client-supplied), so a caller cannot point enumeration
    /// at an arbitrary id. Reads only — no Whisparr-side mutation.
    /// </summary>
    internal async Task<IResult> DiscoveryEntityAsync(
        DiscoveryEntityRequest req, WhisparrClient client, StashDbGraphQlClient stashDbClient,
        TpdbClient tpdbClient, CancellationToken ct)
    {
        // Configure-gated on the CALLER principal (a foreground request, NOT RunAsSystem — that seam is reserved
        // for the Anonymous-principal background webhook/poll/job reads). Discovery reaches the stored creds to
        // read Whisparr, so it sits in the same creds-reaching-read tier as the scene-status + monitor endpoints,
        // which also gate on extensions.configure.
        if (req.CoveEntityId is not { } coveEntityId || !TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new ErrorResponse("UNKNOWN_KIND"), statusCode: 400);
        }

        // A client-supplied page is clamped to a valid 1-based index (an over-range/negative value can never
        // loop or error — the read just returns an empty page); omitted means the whole-catalogue re-derive.
        int? page = req.Page is { } requested ? Math.Max(1, requested) : null;

        var computation = await ComputeDiscoveryAsync(
            kind, coveEntityId, client, stashDbClient, tpdbClient, ct, page, req.Query);
        if (computation.Terminal is { } terminal)
        {
            return terminal;
        }

        return Results.Json(
            new DiscoveryResult(
                computation.Missing, computation.EntityName, computation.State, computation.Source, computation.Version,
                computation.NextPage, computation.HasMore, computation.Total, computation.IsParent,
                computation.TotalIsAtLeast, computation.ServerSideSorts, computation.ServerSideFacets,
                computation.WholeSetFacetAxes, computation.FacetOptions, computation.CatalogueTruncated),
            EnumStringResponseJsonOptions);
    }

    /// <summary>
    /// The missing-scene COUNT for the host tab badge — the same diff as <see cref="DiscoveryEntityAsync"/>
    /// reduced to <c>{ count }</c>. Same security posture (configure-gated, caller principal, stored creds only,
    /// server-resolved id).
    /// </summary>
    internal async Task<IResult> DiscoveryCountAsync(
        string? kind, int? entityId, WhisparrClient client, StashDbGraphQlClient stashDbClient,
        TpdbClient tpdbClient, CancellationToken ct)
    {
        // Same creds-reaching-read gate as the entity handler (see DiscoveryEntityAsync): configure on the
        // caller principal, matching the scene-status + monitor tier.
        if (entityId is not { } coveEntityId || !TryParseEntityKind(kind, out var entityKind))
        {
            return Results.Json(new ErrorResponse("UNKNOWN_KIND"), statusCode: 400);
        }

        // A page:1 read (not the whole catalogue) so the direct route can report the source-advertised catalogue
        // total without a full multi-page fetch. A route with no trustworthy total (ThePornDB's placeholder) or a
        // non-served state carries no total, so the count falls back to the diffed size — 0 for the non-served states.
        // No query is passed: the host tab badge is shared chrome, and a badge that moved with one user's private
        // view filter would disagree between users of the same library.
        var computation = await ComputeDiscoveryAsync(entityKind, coveEntityId, client, stashDbClient, tpdbClient, ct, page: 1);
        if (computation.Terminal is { } terminal)
        {
            return terminal;
        }

        return Results.Json(new DiscoveryCountResponse(computation.Total ?? computation.Missing.Count), EnumStringResponseJsonOptions);
    }

    // The shared discovery core both the read handlers AND the /discovery/action endpoint run: resolve stored
    // creds, require an adapter that offers the discovery role (v3 AND v2 do — a version this build can't manage
    // defers with a clear 400, never a 500), resolve the remote id server-side from the Cove entity id in the
    // connected version's id family, resolve the direct metadata credential from Cove's own configured source and
    // route (DiscoveryRouter — the three-outcome decision: direct read, needs-provider-key, or no-source-id),
    // fetch the direct catalogue (memoized) + the exclusions + the reconciliation status index, load the entity's
    // owned scenes on the caller principal, and run the pure diff. The catalogue is transient (TtlCache), never
    // persisted. Whisparr is read for per-scene STATUS (the reconciliation movie index), never for the catalogue.
    // Returns ONE catalogue-minus-owned pass: the projected missing set, the RAW movie index it diffed from (an
    // action needs the raw id the projection drops), the entity name, the discriminated state + source, and the
    // connected version — so the action endpoint validates a source id against the SAME diff the reads render.
    // page (optional, 1-based): when set, the direct route fetches ONE catalogue page — the incremental Missing tab
    // for a read, and for an action the coordinate of the page its row was rendered from — returning a cursor +
    // hasMore + total. When null the whole catalogue is re-derived. Either way the action endpoints' loop-safety
    // validation runs over whatever set comes back: an id absent from it is refused with no mutation, and a wrong
    // or stale coordinate can only narrow what is actionable, never widen it.
    // queryRequest (optional): the untrusted ordering + facet narrowing from the request body. It is normalized
    // once the connected provider is known (the id shape differs per provider) and reaches the CATALOGUE read
    // only — never the subtraction, which stays keyed on ids alone.
    /// <summary>Records the metadata dependency's already-classified outcome for this discovery read.</summary>
    /// <remarks>
    /// This is the only path in the extension that calls a metadata provider, and one code path serves both —
    /// StashDB on v3, ThePornDB on v2 — so a single metadata row is honest on both generations and the record
    /// needs no version axis. Best-effort: a store fault must not fail a discovery read, and it is logged once.
    /// </remarks>
    private Task RecordMetadataHealthAsync(
        bool credentialResolved, WhisparrResultState state, string? reason, CancellationToken ct)
        => HealthStore.TryRecordAsync(
            Store,
            HealthDependency.Metadata,
            HealthOutcome.FromMetadata(credentialResolved, state, reason),
            failure => LogHealthRecordFailed(HealthDependency.Metadata, failure),
            ct);

    private async Task<DiscoveryComputation> ComputeDiscoveryAsync(
        EntityKind kind, int coveEntityId, WhisparrClient client, StashDbGraphQlClient stashDbClient,
        TpdbClient tpdbClient, CancellationToken ct, int? page = null, DiscoveryQueryRequest? queryRequest = null)
    {
        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        var version = string.IsNullOrEmpty(options.SelectedVersion) ? "v3" : options.SelectedVersion;

        // Capability is resolved from the instance's own API description BEFORE selection, so the adapter this
        // handler holds either carries the catalogue role or structurally lacks it.
        var capabilityRead = await CapabilityPort(client).ResolveAsync(baseUrl, version, ct);
        var adapter = AdapterSelector.SelectForVersion(
            options.SelectedVersion, client, WhisparrCapabilityPort.OrAbsent(capabilityRead));
        if (adapter is null)
        {
            return DiscoveryComputation.FromTerminal(VersionUnsupported(), version);
        }

        // The refusal needs a document that ANSWERED. A read that did not arrive says nothing about what this
        // build offers, and this surface never invokes the role — its catalogue comes from the metadata box —
        // so an unreachable Whisparr must reach the outage handling below rather than be reported as a build
        // missing a route. Only a document declaring no such route refuses here.
        if (capabilityRead.IsOk && adapter is not IWhisparrEntityCatalogue)
        {
            return DiscoveryComputation.FromTerminal(CapabilityUnavailable(), version);
        }

        var discoveryAdapter = adapter;

        // The diff keys on the connected version's identity: StashDB on v3, TPDB on v2. Keying on the wrong
        // family would fail to subtract owned scenes and mis-report them as missing.
        var isV2 = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
        var idFamily = isV2 ? DiscoveryIdFamily.Tpdb : DiscoveryIdFamily.StashDb;

        // The metadata source the direct path would read from on the connected version — StashDB on v3, TPDB on
        // v2. Names the source in the noSourceId/needsProviderKey/own-everything copy so an unmonitored view
        // never claims to have read the wrong system.
        var directSource = isV2 ? DiscoverySourceLabel.Tpdb : DiscoverySourceLabel.StashDb;

        // A parent studio (a studio with child sub-studios) is aggregation-only on v3/StashDB — v2/TPDB has no
        // sub-studio analog, so it stays single-site and byte-unchanged. The children are resolved as System: a
        // studio-hierarchy read spans the whole library and is undercounted under the caller principal by
        // CoveContext's per-principal authz filters (the same reason the performer-avatar read runs as System).
        IReadOnlyList<string> childRemoteIds = [];
        FacetOption[]? childStudioOptions = null;
        if (kind == EntityKind.Studio && !isV2)
        {
            var childIdentities = await LoadStudioChildIdentitiesSafeAsync(coveEntityId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
            childRemoteIds = [.. childIdentities.SelectMany(i => i.StashIds).Where(id => !string.IsNullOrEmpty(id))];

            // The sub-studio axis's option list comes from COVE's own hierarchy, not from the provider. These
            // are the very children the catalogue read unions — complete by construction, at no request cost.
            // A child whose name Cove does not hold labels itself with its id, which is still selectable.
            childStudioOptions = DiscoveryFacetOptionList.Build(childIdentities
                .Select(identity => (
                    Id: identity.StashIds.FirstOrDefault(id => !string.IsNullOrEmpty(id)),
                    identity.Name))
                .Where(child => !string.IsNullOrEmpty(child.Id))
                .Select(child => (child.Id, string.IsNullOrEmpty(child.Name) ? child.Id : child.Name)));
        }

        var isParentStudio = childRemoteIds.Count > 0;

        // Both the tag diff and a parent studio's diff subtract the WHOLE owned library (a scene owned under any
        // tag, or under any child sub-studio, is not "missing"), which the caller principal undercounts — so the
        // body runs as System. A non-parent studio and a performer keep the entity-scoped read on the caller
        // principal (unchanged).
        var computation = await WithScopedLibraryAsync<DiscoveryComputation>(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            // Resolve the entity's lookup id SERVER-SIDE from its Cove id (the request never carries a remote id),
            // in the connected version's id family. No id → nothing to enumerate: the honest noSourceId state
            // (never an empty own-everything, never a 500). No presence read informs this — the entity name for a
            // non-served state falls back to the client's generic label.
            var identity = await library.LoadEntityIdentityAsync(kind, coveEntityId, ct);
            var idsForVersion = isV2 ? identity?.TpdbIds : identity?.StashIds;
            var remoteId = idsForVersion?.FirstOrDefault(id => !string.IsNullOrEmpty(id));

            // The direct-metadata credential from Cove's own configured metadata server (zero key entry; Cove is the
            // SOLE catalogue source, there is no extension-side key). The target box follows the connected
            // version — StashDB (v3) or ThePornDB (v2). Server-side only — never in a response, never logged.
            // Resolved at most once, and only when a read is actually going to happen: an entity with no id at all
            // must not pay for a credential lookup it cannot use.
            ResolvedMetadataCredential? resolved = null;
            var credentialEndpoint = isV2 ? options.TpdbEndpoint : options.StashDbEndpoint;

            // A Cove tag rarely stores a source id — Cove's tags are free-form, and only a scraped tag carries
            // one — yet both sources can look a tag up by NAME, so a name match recovers an id the entity never
            // stored and the tab works on an ordinary Cove tag. Asked of the PROVIDER, not branched on version, so
            // v2 and v3 behave identically. Only ever a FALLBACK: a stored id always wins, and a name the source
            // cannot match confidently leaves remoteId empty so the honest no-id state still shows rather than
            // some other tag's catalogue.
            if (string.IsNullOrEmpty(remoteId)
                && kind == EntityKind.Tag
                && !string.IsNullOrWhiteSpace(identity?.Name))
            {
                resolved = await ResolveDirectCredentialAsync(credentialEndpoint);
                if (resolved is not null)
                {
                    var byName = await BuildDirectProvider(isV2, resolved, stashDbClient, tpdbClient)
                        .ResolveIdByNameAsync(kind, identity.Name, ct);
                    if (byName.IsOk && !string.IsNullOrEmpty(byName.Value))
                    {
                        remoteId = byName.Value;
                    }
                }
            }

            if (string.IsNullOrEmpty(remoteId))
            {
                return DiscoveryComputation.FromState(DiscoveryState.NoSourceId, directSource, version, null);
            }

            // A parent studio unions its own id with every child sub-studio's (deduped) so one direct read covers
            // the whole network; every other entity, and a non-parent studio, carries only its own id.
            IReadOnlyList<string> catalogueIds = isParentStudio
                ? [remoteId, .. childRemoteIds.Where(id => !string.Equals(id, remoteId, StringComparison.OrdinalIgnoreCase))]
                : [remoteId];

            resolved ??= await ResolveDirectCredentialAsync(credentialEndpoint);

            // hasDirectKey means "Cove has a matching metadata server credential". No credential → the actionable
            // needsProviderKey state; a resolved credential → the direct read.
            var route = DiscoveryRouter.Decide(hasRemoteId: true, hasDirectKey: resolved is not null);
            if (route == DiscoveryRoute.NeedsProviderKey)
            {
                // Cove has no matching metadata server: the actionable "set up a metadata source in Cove" state,
                // never a misleading empty list. Named for the connected version's box (StashDB on v3, TPDB on v2).
                await RecordMetadataHealthAsync(
                    credentialResolved: false, WhisparrResultState.Ok, reason: null, ct);
                return DiscoveryComputation.FromState(DiscoveryState.NeedsProviderKey, directSource, version, null);
            }

            // route is Direct — a credential resolved, so resolved is non-null here. The box differs by version:
            // StashDB is stash-box GraphQL on v3; ThePornDB is REST on v2 because its stash-box queryScenes is
            // gated (verified live). The cache is keyed on the endpoint the provider actually reads — the REST
            // host for TPDB, not the graphql endpoint Cove stores.
            // route is Direct, so the credential resolved — bind it non-null once for the read below.
            var credential = resolved!;
            var cacheEndpoint = isV2 ? TpdbClient.DefaultRestBaseUrl : credential.Endpoint;
            var directProvider = BuildDirectProvider(
                isV2, credential, stashDbClient, tpdbClient,
                (aggregateKind, aggregateIds, fetch, token) => CachedDirectFacetAggregateAsync(
                    aggregateKind, aggregateIds, version, cacheEndpoint, credential.ApiKey, fetch, token));

            // The untrusted query becomes trusted here, where the connected provider is known: a StashDB filter id
            // is a UUID and a ThePornDB one is digits, and a value of the wrong shape is dropped.
            var query = DiscoveryQueryGuard.Normalize(queryRequest, isV2);

            // The paging cursor for the read path; stays null/false on the whole-catalogue (non-paged) re-derive,
            // so a non-paged response never advertises more.
            WhisparrResult<WhisparrMovie[]> catalogueResult;
            int? nextPage = null;
            var hasMore = false;
            int? total = null;
            var totalIsAtLeast = false;
            var catalogueTruncated = false;
            DiscoveryFacetOptions? facetOptions = null;
            IReadOnlySet<DiscoveryFacetAxis>? wholeSetAxes = null;
            if (page is { } pageIndex)
            {
                // The read path fetches ONE catalogue page (the diffed rows + the advertised total + whether more
                // remains); the full-loop CachedDirectCatalogueAsync below stays the whole-catalogue re-derive the
                // action endpoints run.
                var pageResult = await CachedDirectCataloguePageAsync(
                    directProvider, kind, catalogueIds, version, pageIndex, DirectPageSize, cacheEndpoint,
                    credential.ApiKey, query, ct);
                if (!pageResult.IsOk)
                {
                    // A non-Ok direct read is the distinct sourceUnreachable state on a 200 — a metadata-source
                    // outage is NEVER an empty own-everything.
                    await RecordMetadataHealthAsync(
                        credentialResolved: true, pageResult.State, pageResult.Reason, ct);
                    return DiscoveryComputation.FromState(DiscoveryState.SourceUnreachable, directSource, version, null);
                }

                var directPage = pageResult.Value!;
                catalogueResult = WhisparrResult<WhisparrMovie[]>.Ok(directPage.Movies);
                total = directPage.Total;
                totalIsAtLeast = directPage.TotalIsAtLeast;
                catalogueTruncated = directPage.CatalogueTruncated;
                hasMore = directPage.HasMore;
                nextPage = directPage.HasMore ? pageIndex + 1 : null;

                // A parent studio's STUDIO axis is Cove's own child list, which no provider aggregate is asked
                // for and none could beat. It overlays whatever the page carried on that axis and joins the
                // whole-set claim; every other axis carries exactly what the source itself claimed for THIS read.
                facetOptions = childStudioOptions is { Length: > 0 } children
                    ? (directPage.FacetOptions ?? new DiscoveryFacetOptions()) with { Studios = children }
                    : directPage.FacetOptions;
                wholeSetAxes = childStudioOptions is { Length: > 0 }
                    ? new HashSet<DiscoveryFacetAxis>(
                        directPage.WholeSetAxes ?? Enumerable.Empty<DiscoveryFacetAxis>())
                    {
                        DiscoveryFacetAxis.Studio,
                    }
                    : directPage.WholeSetAxes;
            }
            else
            {
                var directCatalogue = await CachedDirectCatalogueAsync(
                    directProvider, kind, catalogueIds, version, cacheEndpoint, credential.ApiKey, ct);
                catalogueResult = directCatalogue.IsOk
                    ? WhisparrResult<WhisparrMovie[]>.Ok(directCatalogue.Value!.Movies)
                    : WhisparrResult<WhisparrMovie[]>.PropagateFrom(directCatalogue);
                catalogueTruncated = directCatalogue.IsOk && directCatalogue.Value!.Truncated;
                if (!catalogueResult.IsOk)
                {
                    // Load-bearing: a non-Ok direct read is the distinct sourceUnreachable state on a 200 — a
                    // metadata-source outage is NEVER an empty own-everything. (The TtlCache caches only an Ok
                    // result, so a transient failure is not sticky.)
                    await RecordMetadataHealthAsync(
                        credentialResolved: true, catalogueResult.State, catalogueResult.Reason, ct);
                    return DiscoveryComputation.FromState(DiscoveryState.SourceUnreachable, directSource, version, null);
                }
            }

            if (isV2)
            {
                LogTpdbCatalogueRead(catalogueResult.Value!.Length);
            }
            else
            {
                LogStashDbCatalogueRead(catalogueResult.Value!.Length);
            }

            await RecordMetadataHealthAsync(credentialResolved: true, WhisparrResultState.Ok, reason: null, ct);

            var catalogue = catalogueResult.Value!;

            // v2 has no exclusion surface (IWhisparrExclusions is v3-only, its rows TPDB-keyed and uncorrelatable
            // to a Cove scene), so exclusions apply only on v3; on v2 the diff runs against an empty exclusion set.
            WhisparrExclusion[] exclusions = [];
            if (discoveryAdapter is V3Adapter v3Adapter)
            {
                var exclusionsResult = await CachedExclusionsAsync(v3Adapter, version, baseUrl, apiKey, ct);
                exclusions = exclusionsResult.IsOk ? exclusionsResult.Value! : [];
            }

            // A parent studio's union spans child sub-studios, so a scene Cove owns under ANY child must not read
            // missing — and a tag's owned set is never tag-scoped for the same reason. Both would otherwise be a
            // whole-library read, so they resolve only the ids THIS catalogue page could be subtracted by. A
            // non-parent studio/performer is already entity-bounded and keeps the video read.
            var ownedIds = isParentStudio || kind == EntityKind.Tag
                ? await library.LoadOwnedRemoteIdsAsync(
                    idFamily, DiscoveryService.CatalogueCandidateIds(catalogue, idFamily), ct)
                : DiscoveryService.BuildOwnedIdSet(
                    await library.LoadVideosForEntityAsync(kind, coveEntityId, ct), idFamily);

            // The entity display name comes off the catalogue's studioTitle — correct for a studio (every
            // attributed row carries that one studio's title) but NOT for a performer (whose rows span many
            // studios), so it is derived for the studio kind only; a performer falls back to the UI's generic
            // "this performer".
            var entityName = kind == EntityKind.Studio ? EntityNameFromCatalogue(catalogue) : null;

            // The direct catalogue rows are synthesized entries (Id 0) whose status must NOT be read from them —
            // a card's Whisparr status comes from the RECONCILIATION movie set (Whisparr for STATUS, never for the
            // catalogue): a scene that is a monitored Whisparr movie reads wanted, an unmonitored one unmonitored,
            // and one Whisparr has no row for reads notAdded. On v3 that index is the cached movie list keyed by
            // the StashDB id (the same key the direct rows carry). A null index is the ABSENCE of authority and the
            // diff abstains on every row for it: the v2 TPDB path carries no StashDB-keyed reconciliation index,
            // and a v3 movie-set read that did not return Ok knows nothing about any scene. The catalogue still
            // renders in both cases.
            IReadOnlyDictionary<string, WhisparrMovie>? statusIndex = null;
            if (!isV2 && discoveryAdapter is V3Adapter v3ForStatus)
            {
                var reconciliation = await CachedMoviesAsync(v3ForStatus, version, baseUrl, apiKey, ct);
                if (reconciliation.IsOk)
                {
                    statusIndex = SceneStatusProjector.BuildMovieIndex(reconciliation.Value!);
                }
            }

            // A direct catalogue row carries the metadata source's own performer avatars, so it needs no Cove
            // resolution — the resolver stays empty on this path.
            var missing = DiscoveryService.Diff(
                catalogue, ownedIds, exclusions, entityName, idFamily, statusIndex, PerformerImageResolver.Empty);

            LogDiscoveryRead(missing.Count);
            // The index a /discovery/action search resolves a scene's Whisparr movie id from. On v3 that is the
            // same reconciliation index the status projection above reads: only a movie Whisparr actually holds
            // carries a real id, and every direct-catalogue row is synthesized with Id 0, which resolves to no
            // added movie and leaves the search a handled no-op. One cached movie read therefore backs both
            // indexes. On v2 the catalogue IS the index: a v2 discovery scene is a synthesized episode (ItemType
            // "v2scene", not "scene"), which BuildMovieIndex keys no foreignId for, and keying by foreignId
            // recovers the episode id (WhisparrMovie.Id) the EpisodeSearch needs.
            var actionIndex = ActionMovieIndex(isV2, catalogue, statusIndex);

            // A served catalogue (possibly empty — the honest own-everything), tagged with its direct source so
            // the UI's own-everything copy names StashDB (v3) or ThePornDB (v2). The raw movie index rides
            // alongside so the action endpoint can validate a source id against this exact diff. The paging cursor
            // rides along for the read path (null/false on the whole-catalogue re-derive; the direct source pages
            // natively at the source).
            return DiscoveryComputation.Served(
                    missing, actionIndex, entityName, directSource, version, nextPage, hasMore, total)
                with
            {
                TotalIsAtLeast = totalIsAtLeast,
                CatalogueTruncated = catalogueTruncated,
                // The provider's OWN declaration, projected verbatim. The client cannot infer it from the version:
                // a source that declares nothing supports nothing, which is what the empty list says.
                ServerSideSorts = DiscoveryQueryGuard.SortWireNames(directProvider.ServerSideSorts),
                ServerSideFacets = DiscoveryQueryGuard.FacetWireNames(directProvider.ServerSideFacets),
                // The PER-READ claim, taken from the page this read served and never from the source's per-kind
                // capability. The capability answers "could a studio page do this"; only the page knows whether
                // an aggregate was reachable, covered the entity, and fitted — and a control is worded from what
                // this read actually delivered.
                WholeSetFacetAxes = wholeSetAxes is null
                    ? null
                    : DiscoveryQueryGuard.FacetWireNames(wholeSetAxes),
                FacetOptions = facetOptions,
            };
        }, asSystem: kind == EntityKind.Tag || isParentStudio);

        // The parent signal rides out on the computation so the client's child-studio facet can gate on it; an
        // early terminal (unsupported version) short-circuits before this point and stays the default false.
        return computation with { IsParent = isParentStudio };
    }

    // ONE catalogue-minus-owned pass, shared by the read handlers and the /discovery/action endpoint. Exactly one
    // shape holds: a Terminal read outcome the handlers return verbatim (the 400 unsupported-version), OR a
    // projected result — a non-served discriminated State (noSourceId / needsProviderKey / sourceUnreachable,
    // empty Missing) or a Served catalogue (the diffed Missing set + the RAW MovieIndex it subtracted from, so an
    // action can recover the Whisparr id the projection drops). EntityName / Source / Version carry the display
    // facts either way.
    private readonly record struct DiscoveryComputation(
        IResult? Terminal,
        IReadOnlyList<MissingScene> Missing,
        IReadOnlyDictionary<string, WhisparrMovie> MovieIndex,
        string? EntityName,
        string State,
        string Source,
        string Version,
        int? NextPage,
        bool HasMore,
        int? Total)
    {
        // Whether the studio unioned child sub-studios into this catalogue — set once on the resolved computation
        // (never by a factory), so the default false covers every non-studio kind and a non-parent studio.
        public bool IsParent { get; init; }

        // Whether Total is a lower bound because the source saturated its count. Set from the served page; the
        // default false covers every non-served state, which carries no total at all.
        public bool TotalIsAtLeast { get; init; }

        /// <summary>The source catalogue was cut short by its page ceiling, so "missing" is a lower bound.</summary>
        public bool CatalogueTruncated { get; init; }

        // The orderings the direct provider applied over the whole catalogue, in the wire vocabulary. Set on the
        // served computation from the provider's own declaration; null on every non-served state, where no
        // provider was reached and no capability can honestly be claimed.
        public string[]? ServerSideSorts { get; init; }

        // The narrowing axes the direct provider applies server-side (a capability), the axes whose option list
        // this READ served from a whole-set aggregate, and the option lists themselves. All null on every
        // non-served state and on the whole-catalogue re-derive, where no page was served and nothing about the
        // origin of its options can honestly be claimed.
        public string[]? ServerSideFacets { get; init; }

        public string[]? WholeSetFacetAxes { get; init; }

        public DiscoveryFacetOptions? FacetOptions { get; init; }

        private static readonly IReadOnlyList<MissingScene> NoScenes = [];
        private static readonly IReadOnlyDictionary<string, WhisparrMovie> NoMovies =
            new Dictionary<string, WhisparrMovie>(0);

        public static DiscoveryComputation FromTerminal(IResult terminal, string version)
            => new(terminal, NoScenes, NoMovies, null, DiscoveryState.Ok, DiscoverySourceLabel.Whisparr, version, null, false, null);

        public static DiscoveryComputation FromState(string state, string source, string version, string? entityName)
            => new(null, NoScenes, NoMovies, entityName, state, source, version, null, false, null);

        public static DiscoveryComputation Served(
            IReadOnlyList<MissingScene> missing, IReadOnlyDictionary<string, WhisparrMovie> movieIndex,
            string? entityName, string source, string version,
            int? nextPage = null, bool hasMore = false, int? total = null)
            => new(null, missing, movieIndex, entityName, DiscoveryState.Ok, source, version, nextPage, hasMore, total);
    }

    // The one place the Cove config type is touched: resolve the direct-metadata credential for targetEndpoint
    // from Cove's own configured metadata server (Cove is the sole source), else null. GetService (NOT
    // GetRequiredService) so a host with no CoveConfiguration registered yields no candidates instead of throwing.
    // The direct provider for the connected version — ThePornDB (REST) on v2, StashDB (stash-box GraphQL) on v3.
    // One construction site keeps the name-resolution fallback and the catalogue read on the same provider and
    // endpoint.
    // aggregateMemo is the slot the source reads its whole-set facet aggregates through. Omitted (the name
    // resolution site, which reads no catalogue), the source reads no aggregate at all — never an uncached one.
    private static IDiscoverySource BuildDirectProvider(
        bool isV2, ResolvedMetadataCredential resolved,
        StashDbGraphQlClient stashDbClient, TpdbClient tpdbClient,
        DiscoveryAggregateMemo? aggregateMemo = null)
        => isV2
            ? new TpdbDiscoverySource(
                tpdbClient, TpdbClient.DefaultRestBaseUrl, resolved.ApiKey, resolved.MaxRequestsPerMinute)
            : new StashDbDiscoverySource(
                stashDbClient, resolved.Endpoint, resolved.ApiKey, resolved.MaxRequestsPerMinute, aggregateMemo);

    private async Task<ResolvedMetadataCredential?> ResolveDirectCredentialAsync(string targetEndpoint)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        MetadataServerCandidate[] candidates =
            scope.ServiceProvider.GetService<CoveConfiguration>()?.Scraping.MetadataServers is { } servers
                ? [.. servers.Select(s => new MetadataServerCandidate(s.Endpoint, s.ApiKey, s.MaxRequestsPerMinute))]
                : [];
        return MetadataCredentialResolver.Resolve(candidates, targetEndpoint);
    }

    // The entity display name from the attributed catalogue: a studio's rows all carry its studioTitle (v3 the
    // studio title, v2 the site title stamped by the discovery synth), so the first non-empty one names the
    // entity without a second lookup. Null when the catalogue is empty (the UI falls back to a generic
    // "this studio"/"this performer").
    private static string? EntityNameFromCatalogue(IReadOnlyList<WhisparrMovie> catalogue)
    {
        foreach (var movie in catalogue)
        {
            if (!string.IsNullOrEmpty(movie.StudioTitle))
            {
                return movie.StudioTitle;
            }
        }

        return null;
    }

    // Which already-fetched collection the action's movie index derives from. v3 resolves against the
    // reconciliation movie set, the only collection carrying a real Whisparr id; v2 against the TPDB-keyed
    // catalogue, the only place a v2 episode id exists. An absent or empty reconciliation index (v2, or a v3
    // movie-set read that did not return Ok) leaves every id unresolvable, and an unresolvable id never grabs.
    internal static IReadOnlyDictionary<string, WhisparrMovie> ActionMovieIndex(
        bool isV2,
        IReadOnlyList<WhisparrMovie> catalogue,
        IReadOnlyDictionary<string, WhisparrMovie>? reconciliationIndex)
        => isV2
            ? BuildTpdbMovieIndex(catalogue)
            : reconciliationIndex ?? ReadOnlyDictionary<string, WhisparrMovie>.Empty;

    // v2 episodes need a separate index: SceneStatusProjector.BuildMovieIndex keys a foreignId only for a
    // "scene"-typed row, but a v2 discovery episode is "v2scene", so it would carry no key — leaving a v2 search
    // unable to resolve the episode id. First row wins on a duplicate; a row with no foreignId is unresolvable.
    private static Dictionary<string, WhisparrMovie> BuildTpdbMovieIndex(IReadOnlyList<WhisparrMovie> catalogue)
    {
        var index = new Dictionary<string, WhisparrMovie>(StringComparer.OrdinalIgnoreCase);
        foreach (var movie in catalogue)
        {
            if (!string.IsNullOrEmpty(movie.ForeignId))
            {
                index.TryAdd(movie.ForeignId, movie);
            }
        }

        return index;
    }
}
