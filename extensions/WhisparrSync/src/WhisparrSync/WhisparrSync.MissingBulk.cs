using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WhisparrSync.Contracts;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    /// <summary>Marks a page's ticked scenes as wanted, as one background run.</summary>
    internal static Task<Results<Ok<MissingBulkEnqueued>, BadRequest, ForbiddenCode>>
        EnqueueMissingBulkMonitorAsync(
            string kind, int coveId, MissingBulkRequest request, ICurrentPrincipalAccessor principal)
    {
        if (!HasConfigurePermission(principal))
        {
            return Task.FromResult<Results<Ok<MissingBulkEnqueued>, BadRequest, ForbiddenCode>>(
                new ForbiddenCode());
        }

        // A body naming no scene is a request this route cannot express: the route names the Cove
        // entity and the scenes are the only thing the body carries.
        if (!TryReadEntity(kind, coveId, out _) || request is not { ProviderSceneIds.Count: > 0 })
        {
            return Task.FromResult<Results<Ok<MissingBulkEnqueued>, BadRequest, ForbiddenCode>>(
                TypedResults.BadRequest());
        }

        return Task.FromResult<Results<Ok<MissingBulkEnqueued>, BadRequest, ForbiddenCode>>(
            TypedResults.Ok(new MissingBulkEnqueued(null, MissingRefusalKind.NoInstanceConnected)));
    }
}
