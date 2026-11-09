namespace Cove.Extensions.Shared.Tests;

public sealed class MinimalApiPermissionsTests
{
    private const string Required = "videos.write";

    [Fact]
    public void APrincipalHoldingThePermission_IsLetThrough()
    {
        var accessor = FakePrincipalAccessor.WithPermissions(Required);

        Assert.Null(MinimalApiPermissions.Forbidden(accessor, Required));
    }

    [Fact]
    public void APrincipalHoldingADifferentPermission_IsRefused()
    {
        var accessor = FakePrincipalAccessor.WithPermissions("videos.read");

        Assert.NotNull(MinimalApiPermissions.Forbidden(accessor, Required));
    }

    [Fact]
    public void TheAnonymousPrincipal_IsRefused()
    {
        Assert.NotNull(MinimalApiPermissions.Forbidden(FakePrincipalAccessor.None(), Required));
    }

    // The window this gate exists for. A host with its own authentication off hands an extension a
    // principal that carries nothing, and on a fresh install there is no principal at all - so the
    // route's own declaration has already been satisfied and this is the only check left.
    [Fact]
    public void NoPrincipalAtAll_IsRefused_RatherThanTreatedAsUnrestricted()
    {
        var accessor = FakePrincipalAccessor.NullPrincipal();
        Assert.Null(accessor.Current);

        Assert.NotNull(MinimalApiPermissions.Forbidden(accessor, Required));
    }

    // The refusal is the extension's own 403 body rather than a bare status, because the route's
    // emitted document declares that shape and a generated client parses against it.
    [Fact]
    public void TheRefusal_IsTheExtensionsOwnForbiddenProblem()
    {
        var refused = MinimalApiPermissions.Forbidden(FakePrincipalAccessor.None(), Required);

        Assert.NotNull(refused);
        Assert.Equal(403, refused!.Problem.Status);
        Assert.Equal("FORBIDDEN", refused.Problem.Code);
    }
}
