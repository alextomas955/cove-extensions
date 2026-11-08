using Cove.Core.Auth;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// A settable <see cref="ICurrentPrincipalAccessor"/> fake, so a test can hand a handler a principal
/// that has or lacks a permission key without a request pipeline. The host's
/// <c>[RequiresPermission]</c> attribute is an MVC filter and does not apply to a minimal-API route,
/// so what these tests drive is the in-handler gate in <c>MinimalApiPermissions</c>.
/// </summary>
public sealed class FakePrincipalAccessor : ICurrentPrincipalAccessor
{
    public CovePrincipal? Current { get; private set; }

    public void Set(CovePrincipal? principal) => Current = principal;

    /// <summary>
    /// An accessor whose <see cref="Current"/> is a User principal granted exactly the given permission
    /// keys and no roles.
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

    /// <summary>An accessor whose <see cref="Current"/> is the anonymous principal (no permissions) - the deny path.</summary>
    public static FakePrincipalAccessor None()
    {
        var accessor = new FakePrincipalAccessor();
        accessor.Set(CovePrincipal.Anonymous());
        return accessor;
    }

    /// <summary>
    /// An accessor whose <see cref="Current"/> is null - the no-principal arm of the permission gate,
    /// distinct from <see cref="None"/>'s non-null anonymous principal.
    /// </summary>
    public static FakePrincipalAccessor NullPrincipal() => new();
}
