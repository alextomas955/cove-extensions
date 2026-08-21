using Cove.Core.Auth;

namespace Cove.Extensions.Shared;

/// <summary>Shared authorization gate for extension minimal-API endpoints.</summary>
/// <remarks>
/// The host <c>[RequiresPermission]</c> filter is MVC-only and inert on minimal-API endpoints, so
/// every handler must re-check the principal itself. This is the one 403 gate every extension in
/// this repository re-checks through, so the denial is spelled once rather than per handler.
/// </remarks>
public static class MinimalApiPermissions
{
    /// <summary>
    /// Returns a <c>403 FORBIDDEN</c> result when the principal is null or lacks
    /// <paramref name="permission"/>, otherwise <c>null</c> (proceed).
    /// </summary>
    public static ForbiddenCode? Forbidden(ICurrentPrincipalAccessor principal, string permission)
        => principal.Current is null || !principal.Current.Has(permission)
            ? new ForbiddenCode()
            : null;
}
