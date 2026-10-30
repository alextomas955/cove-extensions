using System.Globalization;
using Cove.Core.Auth;

namespace Cove.Extensions.Shared.Tests;

public sealed class EntityAuthorizationTests
{
    private const string Kind = "toy";

    private const string Permission = "toys.read";

    [Fact]
    public async Task OnlyTheIdsThePrincipalHoldsThePermissionOverComeBackAndInTheSuppliedOrder()
    {
        var authz = new DecidingAuthorization { Denied = { 2, 4 } };

        var allowed = await EntityAuthorization.AllowedOnlyAsync(
            authz, Caller(Permission), Kind, Permission, [5, 4, 3, 2, 1], TestContext.Current.CancellationToken);

        Assert.Equal([5, 3, 1], allowed);
        Assert.Equal([(Permission, Kind, 5), (Permission, Kind, 4), (Permission, Kind, 3),
                      (Permission, Kind, 2), (Permission, Kind, 1)], authz.Asked);
    }

    // The wildcard grant is read literally. A helper that expanded it would answer true for a caller
    // whose grant is narrower than the whole library, which drops the per-entity decision.
    [Fact]
    public async Task APrincipalHoldingTheWildcardGetsTheWholeInputWithNoAuthorizationCall()
    {
        var authz = new DecidingAuthorization { Denied = { 2 } };

        var allowed = await EntityAuthorization.AllowedOnlyAsync(
            authz, Caller(Permissions.All), Kind, Permission, [1, 2], TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], allowed);
        Assert.Empty(authz.Asked);
    }

    [Fact]
    public async Task AnEmptySelectionIsAnsweredWithoutAskingAboutAnything()
    {
        var authz = new DecidingAuthorization();

        var allowed = await EntityAuthorization.AllowedOnlyAsync(
            authz, Caller(Permission), Kind, Permission, [], TestContext.Current.CancellationToken);

        Assert.Empty(allowed);
        Assert.Empty(authz.Asked);
    }

    [Fact]
    public void TheCopiedPrincipalCarriesTheCallersGrantsAndIsNotTheSameInstance()
    {
        var caller = Caller(Permission);

        var copied = EntityAuthorization.Snapshot(caller);

        Assert.NotSame(caller, copied);
        Assert.Equal(caller.UserId, copied!.UserId);
        Assert.Equal(caller.Kind, copied.Kind);
        Assert.Contains(Permission, copied.Permissions);
    }

    [Fact]
    public void NoPrincipalCopiesToNoPrincipal()
        => Assert.Null(EntityAuthorization.Snapshot(null));

    private static CovePrincipal Caller(params string[] permissions)
        => FakePrincipalAccessor.WithPermissions(permissions).Current!;

    // Records what it was asked about and denies the ids a case names. The members this helper's
    // caller never reaches throw rather than answering a default.
    private sealed class DecidingAuthorization : IAuthorizationService
    {
        public List<(string Permission, string EntityKind, int EntityId)> Asked { get; } = [];

        public HashSet<int> Denied { get; } = [];

        public Task<AuthorizationResult> AuthorizeAsync(
            CovePrincipal? principal, string permission, EntityRef? entity, CancellationToken ct)
        {
            var named = entity!.Value;
            var id = int.Parse(named.Id, CultureInfo.InvariantCulture);
            Asked.Add((permission, named.Kind, id));

            return Task.FromResult(Denied.Contains(id)
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
}
