using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapMissingEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Read tier: each reaches the one entity the route segment names.
        endpoints.MapGet(MissingPageRoute,
            (string kind, int coveId, int? page, int? perPage, string? sort, string? q,
             string? filters, bool? menusHeld, ICurrentPrincipalAccessor principal,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             ProviderEndpointPort endpoints, MissingPagePlanner planner, CancellationToken ct)
                => ReadMissingPageAsync(
                    kind, coveId, page, perPage, sort, q, filters, menusHeld, principal, options,
                    credentials, client, endpoints, planner, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        endpoints.MapGet(MissingCountRoute,
            (string kind, int coveId, string? q, string? filters,
             ICurrentPrincipalAccessor principal, OptionsStore options, ICredentialPort credentials,
             IWhisparrClient client, ProviderEndpointPort endpoints, MissingPagePlanner planner,
             CancellationToken ct)
                => ReadMissingCountAsync(
                    kind, coveId, q, filters, principal, options, credentials, client, endpoints,
                    planner, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // Read tier: the same entity reach, and the answer is values the metadata source publishes.
        // It composes no write and asks the connected instance nothing.
        endpoints.MapGet(MissingFacetValuesRoute,
            (string kind, int coveId, string facetKey, string? q,
             ICurrentPrincipalAccessor principal, OptionsStore options, MissingPagePlanner planner,
             CancellationToken ct)
                => ReadMissingFacetValuesAsync(
                    kind, coveId, facetKey, q, principal, options, planner, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);
    }

    // The sort, the title search and every facet selection travel to the provider unchanged, so
    // they narrow the catalogue rather than the page that happened to load.
    internal static async Task<Results<Ok<MissingPageView>, BadRequest, ForbiddenCode>>
        ReadMissingPageAsync(
            string kind,
            int coveId,
            int? page,
            int? perPage,
            string? sort,
            string? q,
            string? filters,
            bool? menusHeld,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ProviderEndpointPort endpoints,
            MissingPagePlanner planner,
            ILogger log,
            CancellationToken ct)
    {
        // Re-checked here because the route declaration enforces nothing on a minimal API.
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(kind, coveId, out var entityKind)
            || !TryReadPaging(page, perPage, out var readPage, out var readPerPage))
        {
            return TypedResults.BadRequest();
        }

        var context = await ResolveMissingContextAsync(
                options, credentials, client, endpoints, ct)
            .ConfigureAwait(false);
        if (context is null)
        {
            return TypedResults.Ok(
                RefusedPage(
                    readPage, readPerPage, MissingRefusalKind.NoInstanceConnected, planner));
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
                RefusedPage(
                    readPage, readPerPage, MissingRefusalKind.ProviderUnreachable, planner));
        }
    }

    // The catalogue's own size, never the number missing. A size that cannot be answered is null,
    // so the host draws no badge.
    internal static async Task<Results<Ok<MissingCountView>, BadRequest, ForbiddenCode>>
        ReadMissingCountAsync(
            string kind,
            int coveId,
            string? q,
            string? filters,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ProviderEndpointPort endpoints,
            MissingPagePlanner planner,
            ILogger log,
            CancellationToken ct)
    {
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(kind, coveId, out var entityKind))
        {
            return TypedResults.BadRequest();
        }

        var context = await ResolveMissingContextAsync(
                options, credentials, client, endpoints, ct)
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
            string kind,
            int coveId,
            string facetKey,
            string? q,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            MissingPagePlanner planner,
            ILogger log,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(planner);

        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(kind, coveId, out var entityKind) || string.IsNullOrWhiteSpace(facetKey))
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
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        ProviderEndpointPort endpoints,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(endpoints);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var generation = stored.SelectedGeneration;
        var apiKey = await credentials.ReadAsync(generation, ct).ConfigureAwait(false);

        if (!ConnectionTester.TryReadConnection(
            stored.ConnectionFor(generation)?.Address, apiKey, out var baseAddress, out _))
        {
            return null;
        }

        var capabilities = GenerationCapabilities.For(generation, WhisparrRoleSet.From(client));
        var reading = capabilities
            .Obtain<IWhisparrSceneStatusReading>()
            .Match<IWhisparrSceneStatusReading?>(held => held, _ => null);
        var exclusions = capabilities
            .Obtain<IWhisparrSceneExclusionReading>()
            .Match<IWhisparrSceneExclusionReading?>(held => held, _ => null);

        return new MissingPageContext(
            baseAddress,
            apiKey,
            generation,
            endpoints.Resolve(generation, stored.MetadataProviderEndpoints),
            reading,
            exclusions);
    }

    private static MissingPageView RefusedPage(
        int page, int perPage, MissingRefusalKind refusal, MissingPagePlanner planner)
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
            StatusWasRead: false,
            StatusIsPermanentlyAbsent: false,
            planner.ProviderName);

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
    private static bool TryReadEntity(string kind, int coveId, out WhisparrEntityKind entityKind)
        => Enum.TryParse(kind, ignoreCase: true, out entityKind)
            && Enum.IsDefined(entityKind)
            && coveId > 0;
}
