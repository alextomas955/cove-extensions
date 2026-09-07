using Cove.Core.Auth;

namespace Cove.Extensions.Shared;

/// <summary>In-handler authorization gate for a route whose permission is not fixed at registration.</summary>
/// <remarks>
/// <para>
/// This is the fallback, NOT the default. A route whose permission is known when it is registered
/// declares it to the host instead (<c>RequireCovePermission</c> / <c>RequireCoveEntityAccess</c> from
/// <c>Cove.Sdk</c>), which enforces it in middleware before the extension scope exists and audits every
/// denial. A route must never carry both — one gate, in one place.
/// </para>
/// <para>
/// It survives for the one shape the route conventions cannot express: a permission chosen from the
/// REQUEST BODY. Renamer's endpoints resolve theirs from the submitted entity kind (video vs image
/// permissions), and the host convention reads route values only, deliberately never pre-reading a body.
/// </para>
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
