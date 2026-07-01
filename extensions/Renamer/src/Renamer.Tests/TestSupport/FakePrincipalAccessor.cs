using Cove.Core.Auth;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A settable <see cref="ICurrentPrincipalAccessor"/> fake so the endpoint permission tests
/// can hand a principal that HAS or LACKS <c>videos.write</c>/<c>videos.read</c> without a request
/// pipeline (extension minimal-API endpoints enforce permissions themselves).
/// </summary>
public sealed class FakePrincipalAccessor : ICurrentPrincipalAccessor
{
    public CovePrincipal? Current { get; private set; }

    public void Set(CovePrincipal? principal) => Current = principal;

    /// <summary>
    /// Builds an accessor whose <see cref="Current"/> is a User principal granted exactly the given
    /// permission keys (e.g. <c>"videos.write"</c>). Roles are empty; a dummy UserId/Username is used.
    /// </summary>
    public static FakePrincipalAccessor WithPermissions(params string[] permissions)
    {
        var accessor = new FakePrincipalAccessor();
        accessor.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "test-user",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string>(permissions),
        });
        return accessor;
    }

    /// <summary>An accessor whose <see cref="Current"/> is the anonymous principal (no permissions) — the deny path.</summary>
    public static FakePrincipalAccessor None()
    {
        var accessor = new FakePrincipalAccessor();
        accessor.Set(CovePrincipal.Anonymous());
        return accessor;
    }

    /// <summary>
    /// An accessor whose <see cref="Current"/> is null — the no-principal arm of the permission gate,
    /// distinct from <see cref="None"/>'s non-null anonymous principal.
    /// </summary>
    public static FakePrincipalAccessor NullPrincipal() => new();
}
