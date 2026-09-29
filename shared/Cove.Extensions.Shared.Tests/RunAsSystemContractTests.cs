using Cove.Core.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.Shared.Tests;

public sealed class RunAsSystemContractTests
{
    [Fact]
    public async Task TheGenericOverload_ElevatesForTheBody_ReturnsItsValue_AndRestoresTheCaller()
    {
        var accessor = Caller();
        var caller = accessor.Current;

        PrincipalKind? seenInside = null;
        int returned = await RunAsSystem.RunAsSystemAsync(
            ProviderWith(accessor),
            () =>
            {
                seenInside = accessor.Current?.Kind;
                return Task.FromResult(7);
            });

        Assert.Equal(PrincipalKind.System, seenInside);
        Assert.Equal(7, returned);
        Assert.Same(caller, accessor.Current);
    }

    [Fact]
    public async Task TheVoidOverload_ElevatesForTheBody_AndRestoresTheCaller()
    {
        var accessor = Caller();
        var caller = accessor.Current;

        PrincipalKind? seenInside = null;
        Func<Task> body = () =>
        {
            seenInside = accessor.Current?.Kind;
            return Task.CompletedTask;
        };

        await RunAsSystem.RunAsSystemAsync(ProviderWith(accessor), body);

        Assert.Equal(PrincipalKind.System, seenInside);
        Assert.Same(caller, accessor.Current);
    }

    [Fact]
    public async Task ABodyThatThrows_SurfacesTheException_AndStillRestoresTheCaller()
    {
        var accessor = Caller();
        var caller = accessor.Current;

        PrincipalKind? seenInside = null;
        Func<Task> failing = () =>
        {
            seenInside = accessor.Current?.Kind;
            throw new InvalidOperationException("the body failed");
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsSystem.RunAsSystemAsync(ProviderWith(accessor), failing));

        Assert.Equal("the body failed", thrown.Message);
        Assert.Equal(PrincipalKind.System, seenInside);

        // The restore is the finally's contract and not a courtesy of the happy path: without it a caller
        // whose body failed keeps an elevated principal for the remainder of its own scope.
        Assert.Same(caller, accessor.Current);
    }

    [Fact]
    public async Task APriorPrincipalThatWasAbsent_ComesBackAbsent_AndNotAsADefault()
    {
        // The queued condition at this tier: nothing was set, so nothing is what has to come back.
        // Restoring Anonymous, or leaving System in place, would each be a different bug wearing the
        // same green - which is why the assertion names null rather than any principal at all.
        var accessor = new FakePrincipalAccessor();

        PrincipalKind? seenInside = null;
        await RunAsSystem.RunAsSystemAsync(
            ProviderWith(accessor),
            () =>
            {
                seenInside = accessor.Current?.Kind;
                return Task.FromResult(true);
            });

        Assert.Equal(PrincipalKind.System, seenInside);
        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task AScopeWithNoAccessor_RunsTheBodyUnchanged_AndReturnsItsValue()
    {
        bool ran = false;

        // Nothing to observe from inside, because there is no accessor to observe - so what this case
        // records instead is that the body ran at all, which a silently swallowed body would break.
        int returned = await RunAsSystem.RunAsSystemAsync(
            new ServiceCollection().BuildServiceProvider(),
            () =>
            {
                ran = true;
                return Task.FromResult(11);
            });

        Assert.True(ran);
        Assert.Equal(11, returned);
    }

    // A present caller principal: a user holding no permissions. Present rather than absent so the
    // restore assertions have an instance to name - the absent prior value is its own case above.
    private static FakePrincipalAccessor Caller() => FakePrincipalAccessor.WithPermissions();

    // A provider whose only registration is accessor.
    private static ServiceProvider ProviderWith(ICurrentPrincipalAccessor accessor) =>
        new ServiceCollection().AddSingleton(accessor).BuildServiceProvider();
}
