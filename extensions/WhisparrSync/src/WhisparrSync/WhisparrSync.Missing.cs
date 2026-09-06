using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    /// <summary>One page of what an entity's configured metadata source lists and Cove does not hold.</summary>
    internal static Task<Results<Ok<MissingPageView>, BadRequest, ForbiddenCode>> ReadMissingPageAsync(
        string kind, int coveId, ICurrentPrincipalAccessor principal)
    {
        if (!HasReadPermission(principal))
        {
            return Task.FromResult<Results<Ok<MissingPageView>, BadRequest, ForbiddenCode>>(
                new ForbiddenCode());
        }

        if (!TryReadEntity(kind, coveId, out _))
        {
            return Task.FromResult<Results<Ok<MissingPageView>, BadRequest, ForbiddenCode>>(
                TypedResults.BadRequest());
        }

        return Task.FromResult<Results<Ok<MissingPageView>, BadRequest, ForbiddenCode>>(
            TypedResults.Ok(NoInstanceConnectedPage));
    }

    /// <summary>How many scenes an entity's configured metadata source lists.</summary>
    internal static Task<Results<Ok<MissingCountView>, BadRequest, ForbiddenCode>> ReadMissingCountAsync(
        string kind, int coveId, ICurrentPrincipalAccessor principal)
    {
        if (!HasReadPermission(principal))
        {
            return Task.FromResult<Results<Ok<MissingCountView>, BadRequest, ForbiddenCode>>(
                new ForbiddenCode());
        }

        if (!TryReadEntity(kind, coveId, out _))
        {
            return Task.FromResult<Results<Ok<MissingCountView>, BadRequest, ForbiddenCode>>(
                TypedResults.BadRequest());
        }

        // A non-number, so the host draws no badge at all and states nothing it cannot support.
        return Task.FromResult<Results<Ok<MissingCountView>, BadRequest, ForbiddenCode>>(
            TypedResults.Ok(new MissingCountView(null, false)));
    }

    /// <summary>A page carrying no scenes, and the reason it carries none.</summary>
    private static MissingPageView NoInstanceConnectedPage => new(
        Cards: [],
        CatalogueSize: 0,
        SizeIsLowerBound: false,
        Page: 1,
        PerPage: MissingPerPage,
        LastPage: 1,
        RangeFrom: 0,
        RangeTo: 0,
        Refusal: MissingRefusalKind.NoInstanceConnected,
        Facets: [],
        Sorts: [],
        SortInForce: null,
        StatusWasRead: false,
        StatusIsPermanentlyAbsent: false,
        MonitorAllIsOffered: false);

    /// <summary>How many scenes one page of the catalogue carries.</summary>
    private const int MissingPerPage = 40;

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
