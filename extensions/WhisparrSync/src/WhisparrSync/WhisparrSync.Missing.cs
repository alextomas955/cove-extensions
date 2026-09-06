using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
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

        // Only the studio catalogue is derivable so far. The other kinds answer the same stated
        // refusal a studio with no provider identifier answers, which is narrower coverage rather
        // than a wrong answer.
        if (entityKind != WhisparrEntityKind.Studio)
        {
            return TypedResults.Ok(
                RefusedPage(readPage, readPerPage, MissingRefusalKind.NoProviderIdForEntity));
        }

        var context = await ResolveMissingContextAsync(options, credentials, client, endpoints, ct)
            .ConfigureAwait(false);
        if (context is null)
        {
            return TypedResults.Ok(
                RefusedPage(readPage, readPerPage, MissingRefusalKind.NoInstanceConnected));
        }

        var request = new MissingPageRequest(
            entityKind,
            coveId,
            EntityName: null,
            Aliases: [],
            readPage,
            readPerPage,
            Blank(sort),
            Blank(q),
            MissingFilterForm.Read(filters),
            menusHeld ?? false);

        try
        {
            return TypedResults.Ok(await planner.PlanAsync(request, context, ct).ConfigureAwait(false));
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            WhisparrSyncLog.CatalogueReadContained(log, WhisparrSyncLog.Classify(failure));
            return TypedResults.Ok(
                RefusedPage(readPage, readPerPage, MissingRefusalKind.ProviderUnreachable));
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

        if (entityKind != WhisparrEntityKind.Studio)
        {
            return TypedResults.Ok(NoCount);
        }

        var context = await ResolveMissingContextAsync(options, credentials, client, endpoints, ct)
            .ConfigureAwait(false);
        if (context is null)
        {
            return TypedResults.Ok(NoCount);
        }

        var request = new MissingPageRequest(
            entityKind,
            coveId,
            EntityName: null,
            Aliases: [],
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

    /// <summary>No measurement at all, which draws no badge rather than a zero.</summary>
    private static MissingCountView NoCount => new(null, false);

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
    private static MissingPageView RefusedPage(int page, int perPage, MissingRefusalKind refusal)
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
            MonitorAllIsOffered: false);

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
