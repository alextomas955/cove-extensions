using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

// What a reader asked the catalogue to be narrowed to. Every member travels to the provider
// unchanged, so it narrows the catalogue rather than the page that happened to load. The count
// route binds the same shape and reads only the two members a count depends on.
internal sealed record MissingCountNarrowing(
    [FromQuery(Name = "q")] string? Q,
    [FromQuery(Name = "filters")] string? Filters);

// The paging and the sort a page read adds on top of the narrowing a count shares with it.
internal sealed record MissingNarrowing(
    [FromQuery(Name = "page")] int? Page,
    [FromQuery(Name = "perPage")] int? PerPage,
    [FromQuery(Name = "sort")] string? Sort,
    [FromQuery(Name = "q")] string? Q,
    [FromQuery(Name = "filters")] string? Filters,
    [FromQuery(Name = "menusHeld")] bool? MenusHeld);

public sealed partial class WhisparrSync
{
    private void MapMissingEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Read tier: each reaches the one entity the route segment names.
        endpoints.MapGet(MissingPageRoute,
            ([AsParameters] EntityRoute route, [AsParameters] MissingNarrowing narrowing,
             ICurrentPrincipalAccessor principal, WhisparrAccess whisparr,
             ProviderEndpointPort endpoints, MissingPagePlanner planner, CancellationToken ct)
                => ReadMissingPageAsync(
                    route, narrowing, principal, whisparr, endpoints, planner, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        endpoints.MapGet(MissingCountRoute,
            ([AsParameters] EntityRoute route, [AsParameters] MissingCountNarrowing narrowing,
             ICurrentPrincipalAccessor principal, WhisparrAccess whisparr,
             ProviderEndpointPort endpoints, MissingPagePlanner planner, CancellationToken ct)
                => ReadMissingCountAsync(
                    route, narrowing, principal, whisparr, endpoints, planner, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // Read tier: the same entity reach, and the answer is values the metadata source publishes.
        // It composes no write and asks the connected instance nothing.
        endpoints.MapGet(MissingFacetValuesRoute,
            ([AsParameters] EntityRoute route, string facetKey, string? q,
             ICurrentPrincipalAccessor principal, WhisparrAccess whisparr,
             MissingPagePlanner planner, CancellationToken ct)
                => ReadMissingFacetValuesAsync(
                    route, facetKey, q, principal, whisparr, planner, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);
    }

    // The sort, the title search and every facet selection travel to the provider unchanged, so
    // they narrow the catalogue rather than the page that happened to load.
    internal static async Task<Results<Ok<MissingPageView>, BadRequest, ForbiddenCode>>
        ReadMissingPageAsync(
            EntityRoute route,
            MissingNarrowing narrowing,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            ProviderEndpointPort endpoints,
            MissingPagePlanner planner,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(narrowing);
        var (_, coveId) = route;
        var (page, perPage, sort, q, filters, menusHeld) = narrowing;

        var (_, _, _, log) = whisparr;

        // Re-checked here because the route declaration enforces nothing on a minimal API.
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(route, out var entityKind)
            || !TryReadPaging(page, perPage, out var readPage, out var readPerPage))
        {
            return TypedResults.BadRequest();
        }

        var context = await ResolveMissingContextAsync(
                whisparr, endpoints, ct)
            .ConfigureAwait(false);
        if (context is null)
        {
            return TypedResults.Ok(
                await RefusedPageAsync(
                        readPage, readPerPage, MissingRefusalKind.NoInstanceConnected, planner, ct)
                    .ConfigureAwait(false));
        }

        var request = new MissingPageRequest(
            entityKind,
            coveId,
            readPage,
            readPerPage,
            Blank(sort),
            Blank(q),
            MissingFilterForm.Read(filters),
            menusHeld ?? false);

        try
        {
            return TypedResults.Ok(await planner.PlanAsync(request, context, log, ct).ConfigureAwait(false));
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            WhisparrSyncLog.CatalogueReadContained(log, WhisparrSyncLog.Classify(failure));
            return TypedResults.Ok(
                await RefusedPageAsync(
                        readPage, readPerPage, MissingRefusalKind.ProviderUnreachable, planner, ct)
                    .ConfigureAwait(false));
        }
    }

    // The catalogue's own size, never the number missing. A size that cannot be answered is null,
    // so the host draws no badge.
    internal static async Task<Results<Ok<MissingCountView>, BadRequest, ForbiddenCode>>
        ReadMissingCountAsync(
            EntityRoute route,
            MissingCountNarrowing narrowing,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            ProviderEndpointPort endpoints,
            MissingPagePlanner planner,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(narrowing);
        var (_, coveId) = route;
        var (q, filters) = narrowing;

        var (_, _, _, log) = whisparr;

        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(route, out var entityKind))
        {
            return TypedResults.BadRequest();
        }

        var context = await ResolveMissingContextAsync(
                whisparr, endpoints, ct)
            .ConfigureAwait(false);
        if (context is null)
        {
            return TypedResults.Ok(NoCount);
        }

        var request = new MissingPageRequest(
            entityKind,
            coveId,
            Page: 1,
            MissingPerPage,
            Sort: null,
            Blank(q),
            MissingFilterForm.Read(filters),
            MenusAlreadyHeld: true);

        try
        {
            return TypedResults.Ok(await planner.CountAsync(request, context, ct).ConfigureAwait(false));
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            WhisparrSyncLog.CatalogueReadContained(log, WhisparrSyncLog.Classify(failure));
            return TypedResults.Ok(NoCount);
        }
    }

    // A read of the metadata source alone. It asks the connected instance nothing.
    internal static async Task<Results<Ok<MissingFacetSearchView>, BadRequest, ForbiddenCode>>
        ReadMissingFacetValuesAsync(
            EntityRoute route,
            string facetKey,
            string? q,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            MissingPagePlanner planner,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(whisparr);
        ArgumentNullException.ThrowIfNull(planner);
        var (_, coveId) = route;
        var (options, _, _, log) = whisparr;

        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(route, out var entityKind) || string.IsNullOrWhiteSpace(facetKey))
        {
            return TypedResults.BadRequest();
        }

        var fragment = (q ?? string.Empty).Trim();
        if (fragment.Length < MinimumFacetFragment)
        {
            return TypedResults.Ok(
                MissingPagePlanner.NoFacetValues(MissingFacetSearchOutcome.FragmentTooShort));
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);

        try
        {
            return TypedResults.Ok(
                await planner
                    .SearchFacetValuesAsync(
                        new MissingFacetSearchRequest(entityKind, coveId, facetKey, fragment),
                        stored.SelectedGeneration,
                        ct)
                    .ConfigureAwait(false));
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            WhisparrSyncLog.CatalogueReadContained(log, WhisparrSyncLog.Classify(failure));
            return TypedResults.Ok(
                MissingPagePlanner.NoFacetValues(MissingFacetSearchOutcome.NoAnswer));
        }
    }

    // Two is the shortest fragment that can be a whole value, because a source spells some real
    // tags as two letters. One character would match most of a source's list.
    internal const int MinimumFacetFragment = 2;

    // No measurement, which draws no badge rather than a zero.
    private static MissingCountView NoCount => new(null);

    private const int MissingPerPage = 40;

    // Null where the connection or the provider is absent. A generation holding no status role
    // gives a null reading, stated downstream as a status no retry can establish. One holding no
    // exclusion role subtracts nothing, because it keeps no scene records and so no exclusions.
    private static async Task<MissingPageContext?> ResolveMissingContextAsync(
        WhisparrAccess whisparr,
        ProviderEndpointPort endpoints,
        CancellationToken ct)
    {
        var (options, credentials, instances, _) = whisparr;

        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(endpoints);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var binding = await OutboundPair.ResolveAsync(stored, credentials, ct).ConfigureAwait(false);
        if (binding is null)
        {
            return null;
        }

        var instance = instances.Bound(binding);
        var exclusions = instance as IWhisparrSceneExclusionReading;
        var catalogue = instance as IWhisparrEntityCatalogueReading;

        return new MissingPageContext(
            binding,
            endpoints.Resolve(binding.Generation, stored.MetadataProviderEndpoints),
            exclusions,
            catalogue);
    }

    private static async Task<MissingPageView> RefusedPageAsync(
        int page,
        int perPage,
        MissingRefusalKind refusal,
        MissingPagePlanner planner,
        CancellationToken ct)
        => new(
            Cards: [],
            CatalogueSize: 0,
            SizeIsLowerBound: false,
            page,
            perPage,
            LastPage: 1,
            RangeFrom: 0,
            RangeTo: 0,
            refusal,
            Facets: [],
            Sorts: [],
            SortInForce: null,
            await planner.ProviderNameAsync(ct).ConfigureAwait(false));

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    // A page size above the bound is refused, not clamped. Clamping would answer a different page
    // from the one asked for, under a range the caller would read as the range it named.
    private static bool TryReadPaging(int? page, int? perPage, out int readPage, out int readPerPage)
    {
        readPage = page ?? 1;
        readPerPage = perPage ?? MissingPerPage;
        return readPage >= 1 && readPerPage >= 1 && readPerPage <= MissingPerPage;
    }

    // The parse succeeds for an integer that names no member, and every arm reading a kind throws
    // for one it cannot express, so IsDefined is checked too. Without it, route input reaches a
    // throw inside a handler whose declared results hold no failure.
    private static bool TryReadEntity(EntityRoute route, out WhisparrEntityKind entityKind)
        => Enum.TryParse(route.Kind, ignoreCase: true, out entityKind)
            && Enum.IsDefined(entityKind)
            && route.CoveId > 0;
}
