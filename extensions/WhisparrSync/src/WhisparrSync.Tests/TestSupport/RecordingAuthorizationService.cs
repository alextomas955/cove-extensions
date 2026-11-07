using System.Globalization;
using Cove.Core.Auth;

namespace WhisparrSync.Tests.TestSupport;

// Records every entity the extension asks about and denies the ones a case names. The members this
// product never calls throw rather than answering a default, so a path that started using one is
// visible instead of silently allowed.
internal sealed class RecordingAuthorizationService : IAuthorizationService
{
    public List<(string Permission, string EntityKind, int EntityId)> Asked { get; } = [];

    // The entities to deny; anything absent is allowed.
    public HashSet<(string EntityKind, int EntityId)> Denied { get; } = [];

    public RecordingAuthorizationService Denying(string entityKind, int entityId)
    {
        Denied.Add((entityKind, entityId));
        return this;
    }

    public Task<AuthorizationResult> AuthorizeAsync(
        CovePrincipal? principal, string permission, EntityRef? entity, CancellationToken ct)
    {
        if (entity is not { } target)
        {
            return Task.FromResult(AuthorizationResult.Allow());
        }

        var id = int.Parse(target.Id, CultureInfo.InvariantCulture);
        Asked.Add((permission, target.Kind, id));

        return Task.FromResult(Denied.Contains((target.Kind, id))
            ? AuthorizationResult.Deny("denied by test", permission)
            : AuthorizationResult.Allow());
    }

    public async Task<IReadOnlyList<AuthorizationResult>> AuthorizeManyAsync(
        CovePrincipal? principal, string permission, IReadOnlyList<EntityRef> entities, CancellationToken ct)
    {
        var results = new List<AuthorizationResult>(entities.Count);
        foreach (var entity in entities)
        {
            results.Add(await AuthorizeAsync(principal, permission, entity, ct));
        }

        return results;
    }

    public AuthorizationResult Authorize(CovePrincipal? principal, string permission, EntityRef? entity = null)
        => throw new NotImplementedException();

    public void Require(CovePrincipal? principal, string permission, EntityRef? entity = null)
        => throw new NotImplementedException();

    public bool Has(CovePrincipal? principal, string permission) => throw new NotImplementedException();
}
