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
        // Entity-scoped reads: the reach of each is the one Cove entity the route segment names, so
        // the read tier expresses it.
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

        // The same read tier as the page and the count beside it: the reach is the one Cove entity
        // the route segment names, and the answer is a list of values the metadata source already
        // publishes. It composes no write and asks the connected instance nothing.
        endpoints.MapGet(MissingFacetValuesRoute,
            (string kind, int coveId, string facetKey, string? q,
             ICurrentPrincipalAccessor principal, OptionsStore options, MissingPagePlanner planner,
             CancellationToken ct)
                => ReadMissingFacetValuesAsync(
                    kind, coveId, facetKey, q, principal, options, planner, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);
    }

    /// <summary>One page of what an entity's configured metadata source lists and Cove does not hold.</summary>
    /// <remarks>
    /// The ordering, the title search and every facet selection are bound from the query string and
    /// travel to the provider unchanged, so what narrows is the catalogue rather than the page that
    /// happened to load.
    /// </remarks>
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
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
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

    /// <summary>How many scenes an entity's configured metadata source lists.</summary>
    /// <remarks>
    /// The catalogue's own size, which is the figure the count line states, and never the number
    /// missing. A size that cannot be answered is null, so the host draws no badge at all and the
    /// reason is stated inside the tab where there is room for a sentence.
    /// </remarks>
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

    /// <summary>The values of one facet that match what a reader typed.</summary>
    /// <remarks>
    /// A read of the metadata source alone. It composes no write, and it asks the connected instance
    /// nothing: which values a source lists is not a fact the instance holds.
    /// </remarks>
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

    /// <summary>The shortest fragment a facet lookup carries to the metadata source.</summary>
    /// <remarks>
    /// One character matches most of a source's list, so the answer would be a page of an
    /// arbitrary slice rather than the values the reader means, at the cost of a request per menu.
    /// Two is the shortest fragment that can be a whole value: a source spells real tags as two
    /// letters, so a higher floor would refuse a value that exists.
    /// </remarks>
    internal const int MinimumFacetFragment = 2;

    /// <summary>No measurement at all, which draws no badge rather than a zero.</summary>
    private static MissingCountView NoCount => new(null);

    /// <summary>How many scenes one page of the catalogue carries.</summary>
    private const int MissingPerPage = 40;

    /// <summary>
    /// The connection and provider a catalogue read runs against, or null where either is absent.
    /// </summary>
    /// <remarks>
    /// The status and exclusion roles are obtained from the connected generation's capability set. A
    /// generation holding no status role hands back null, which the derivation states as a status no
    /// retry can establish; one holding no exclusion role subtracts nothing, because it keeps no
    /// scene records and so keeps no exclusions.
    /// </remarks>
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

    /// <summary>A page carrying no scenes, and the reason it carries none.</summary>
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

    /// <summary>
    /// The page and page size the caller asked for, or that they asked for one outside the bound.
    /// </summary>
    /// <remarks>
    /// A page size above the bound is refused rather than clamped. Clamped silently the answer would
    /// describe a different page from the one asked for, under a range the caller would read as the
    /// range it named.
    /// </remarks>
    private static bool TryReadPaging(int? page, int? perPage, out int readPage, out int readPerPage)
    {
        readPage = page ?? 1;
        readPerPage = perPage ?? MissingPerPage;
        return readPage >= 1 && readPerPage >= 1 && readPerPage <= MissingPerPage;
    }

    /// <summary>
    /// The entity the route segments name, or that they name none this product expresses.
    /// </summary>
    /// <remarks>
    /// The kind's two halves are one expression. The parse succeeds for an integer naming no member,
    /// and every arm reading a kind switches on it and throws for one it cannot express, so the parse
    /// alone lets untrusted route input reach a throw inside a handler whose declared results hold no
    /// failure.
    /// </remarks>
    private static bool TryReadEntity(string kind, int coveId, out WhisparrEntityKind entityKind)
        => Enum.TryParse(kind, ignoreCase: true, out entityKind)
            && Enum.IsDefined(entityKind)
            && coveId > 0;
}
