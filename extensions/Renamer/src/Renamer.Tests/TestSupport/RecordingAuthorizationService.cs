using Cove.Core.Auth;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Records every entity the extension asks about and denies the ones a test names. All other members
/// are unused and throw.
/// </summary>
/// <remarks>
/// <c>AuthorizeManyAsync</c> counts the batch and then fans out to <c>AuthorizeAsync</c>, so every
/// ask still funnels through one place.
/// </remarks>
public sealed class RecordingAuthorizationService : IAuthorizationService
{
    public List<(string Permission, string EntityKind, int EntityId)> Asked { get; } = [];

    /// <summary>How many batch calls were made, whatever each batch's size.</summary>
    public int BatchCalls { get; private set; }

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

    public async Task<IReadOnlyList<AuthorizationResult>> AuthorizeManyAsync(
        CovePrincipal? principal, string permission, IReadOnlyList<EntityRef> entities, CancellationToken ct)
    {
        BatchCalls++;

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
