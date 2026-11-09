using Cove.Core.Auth;

namespace Cove.Extensions.Shared;

/// <summary>In-handler authorization gate, returning the extension's own 403 body.</summary>
/// <remarks>
/// A route declares its requirement with the SDK's <c>RequireCovePermission</c>, and the host's
/// extension-endpoint authorization middleware refuses the request before the handler runs. A handler
/// still checks here, for two reasons.
/// <para>
/// The route metadata is fixed at registration, so it cannot name a permission chosen from the request
/// body or from stored data - the read permission for the kind a caller posted, for instance. Only the
/// handler can narrow to that.
/// </para>
/// <para>
/// The host middleware returns early when Cove's own auth is switched off, and the principal it hands
/// an auth-disabled install carries no permission at all until an owner account exists. The handler
/// check is the only gate in that window, so dropping it where it looks to duplicate the route's
/// declaration opens the endpoint on a fresh auth-disabled host.
/// </para>
/// <para>
/// The <see cref="ForbiddenProblem"/> return arm is also what publishes the 403 schema in the emitted wire
/// document; the host middleware's own denial body is a different shape.
/// </para>
/// </remarks>
public static class MinimalApiPermissions
{
    /// <summary>
    /// Returns a <c>403 FORBIDDEN</c> result when the principal is null or lacks
    /// <paramref name="permission"/>, otherwise <c>null</c> (proceed).
    /// </summary>
    public static ForbiddenProblem? Forbidden(ICurrentPrincipalAccessor principal, string permission)
        => principal.Current is null || !principal.Current.Has(permission)
            ? new ForbiddenProblem()
            : null;
}
