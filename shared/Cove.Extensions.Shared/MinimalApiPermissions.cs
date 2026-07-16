using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;

namespace Cove.Extensions.Shared;

/// <summary>Shared authorization gate for extension minimal-API endpoints.</summary>
/// <remarks>
/// The host <c>[RequiresPermission]</c> filter is MVC-only and inert on minimal-API endpoints, so
/// every handler must re-check the principal itself. This centralizes the one 403 gate both
/// extensions use.
/// </remarks>
public static class MinimalApiPermissions
{
    /// <summary>
    /// Returns a <c>403 FORBIDDEN</c> result when the principal is null or lacks
    /// <paramref name="permission"/>, otherwise <c>null</c> (proceed).
    /// </summary>
    public static IResult? Forbidden(ICurrentPrincipalAccessor principal, string permission)
        => principal.Current is null || !principal.Current.Has(permission)
            ? Results.Json(new { code = "FORBIDDEN" }, statusCode: 403)
            : null;
}
