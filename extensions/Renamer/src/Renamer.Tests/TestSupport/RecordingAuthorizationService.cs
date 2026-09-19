using Cove.Core.Auth;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Records every entity the extension asks about and denies the ones a test names. All other members
/// are unused and throw.
/// </summary>
/// <remarks>
/// Only <c>AuthorizeAsync</c> is implemented, so the interface's default <c>AuthorizeManyAsync</c>
/// funnels every ask through one place.
/// </remarks>
public sealed class RecordingAuthorizationService : IAuthorizationService
{
    public List<(string Permission, string EntityKind, int EntityId)> Asked { get; } = [];

    public CovePrincipal? LastPrincipal { get; private set; }

    /// <summary>The entities to deny; anything absent is allowed.</summary>
    public HashSet<(string EntityKind, int EntityId)> Denied { get; } = [];

    public Task<AuthorizationResult> AuthorizeAsync(
        CovePrincipal? principal, string permission, EntityRef? entity, CancellationToken ct)
    {
        LastPrincipal = principal;

        if (entity is not { } target)
        {
            return Task.FromResult(AuthorizationResult.Allow());
        }

        int id = int.Parse(target.Id, System.Globalization.CultureInfo.InvariantCulture);
        Asked.Add((permission, target.Kind, id));

        return Task.FromResult(Denied.Contains((target.Kind, id))
            ? AuthorizationResult.Deny("denied by test", permission)
            : AuthorizationResult.Allow());
    }

    public AuthorizationResult Authorize(CovePrincipal? principal, string permission, EntityRef? entity = null)
        => throw new NotImplementedException();

    public void Require(CovePrincipal? principal, string permission, EntityRef? entity = null)
        => throw new NotImplementedException();

    public bool Has(CovePrincipal? principal, string permission) => throw new NotImplementedException();
}
