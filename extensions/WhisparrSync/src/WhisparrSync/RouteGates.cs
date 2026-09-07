using Cove.Core.Auth;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;

namespace WhisparrSync;

/// <summary>The access tier each extension route declares to the host, named once.</summary>
/// <remarks>
/// <para>
/// Cove enforces these declarations in host middleware before the extension request scope exists, so a
/// denied call never reaches extension code. An extension route that declares NOTHING is anonymous for
/// backward compatibility (the host logs a startup warning naming it), which is why every route here
/// carries exactly one of these three — the tier is a declaration, never a handler's own re-check.
/// </para>
/// <para>
/// Naming the tiers rather than repeating the underlying permission at each route keeps the audited
/// vocabulary ("read-gated" / "configure-gated") the same in the code, the endpoint table and the tests
/// that assert coverage.
/// </para>
/// </remarks>
internal static class RouteGates
{
    /// <summary>A side-effect-free projection that reaches no credential and makes no outbound call.</summary>
    public static TRoute ReadGated<TRoute>(this TRoute route)
        where TRoute : IEndpointConventionBuilder
        => route.RequireCovePermission(Permissions.ExtensionsRead);

    /// <summary>
    /// Reaches the stored Whisparr credentials or makes an outbound call, so it sits at the configure tier
    /// (⊇ read) even when it only reads.
    /// </summary>
    public static TRoute ConfigureGated<TRoute>(this TRoute route)
        where TRoute : IEndpointConventionBuilder
        => route.RequireCovePermission(Permissions.ExtensionsConfigure);

    /// <summary>
    /// Inbound from Whisparr, which holds no Cove principal: the shared webhook secret is the whole auth,
    /// checked in constant time before the body is parsed.
    /// </summary>
    public static TRoute TokenGated<TRoute>(this TRoute route)
        where TRoute : IEndpointConventionBuilder
        => route.AllowCoveAnonymous();
}
