using Cove.Core.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.Shared.Tests;

// The scope-creating half of RunAsSystem, which a detached body uses because it has no scope of its
// own. RunAsSystemContractTests covers the elevation span itself; what is only true here is that the
// body receives the new scope's services, and that the scope outlives the restore and no longer.
public sealed class RunInSystemScopeTests
{
    // One accessor for the whole provider, so a body can observe the elevation from outside its scope.
    private static ServiceProvider ProviderWith(ICurrentPrincipalAccessor accessor) =>
        new ServiceCollection()
            .AddSingleton(accessor)
            .AddScoped<ScopedMarker>()
            .BuildServiceProvider();

    private sealed class ScopedMarker : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task TheBody_RunsElevated_OverTheNewScopesServices_AndReturnsItsValue()
    {
        var accessor = FakePrincipalAccessor.WithPermissions();
        var caller = accessor.Current;
        await using var provider = ProviderWith(accessor);

        PrincipalKind? seenInside = null;
        IServiceProvider? handed = null;

        int returned = await RunAsSystem.RunInSystemScopeAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            services =>
            {
                seenInside = accessor.Current?.Kind;
                handed = services;
                return Task.FromResult(5);
            });

        Assert.Equal(PrincipalKind.System, seenInside);
        Assert.Equal(5, returned);
        Assert.NotNull(handed);

        // The body's own scope, not the root provider: resolving a scoped service off the root throws,
        // so a caller handed the root would fail at the first scoped resolve rather than here.
        Assert.NotSame(provider, handed);
        Assert.Same(caller, accessor.Current);
    }

    [Fact]
    public async Task TheVoidOverload_RunsTheBodyElevated_AndRestoresTheCaller()
    {
        var accessor = FakePrincipalAccessor.WithPermissions();
        var caller = accessor.Current;
        await using var provider = ProviderWith(accessor);

        PrincipalKind? seenInside = null;
        await RunAsSystem.RunInSystemScopeAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            services =>
            {
                seenInside = accessor.Current?.Kind;
                return Task.CompletedTask;
            });

        Assert.Equal(PrincipalKind.System, seenInside);
        Assert.Same(caller, accessor.Current);
    }

    [Fact]
    public async Task TheScope_IsDisposedWhenTheBodyCompletes()
    {
        var accessor = FakePrincipalAccessor.WithPermissions();
        await using var provider = ProviderWith(accessor);

        ScopedMarker? resolved = null;
        await RunAsSystem.RunInSystemScopeAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            services =>
            {
                resolved = services.GetRequiredService<ScopedMarker>();
                Assert.False(resolved.Disposed);
                return Task.CompletedTask;
            });

        // Stated on the member: anything resolved from the scope, a DbContext included, must not
        // outlive the returned task.
        Assert.NotNull(resolved);
        Assert.True(resolved!.Disposed);
    }

    [Fact]
    public async Task ABodyThatThrows_SurfacesTheException_AndStillRestoresTheCaller_AndDisposesTheScope()
    {
        var accessor = FakePrincipalAccessor.WithPermissions();
        var caller = accessor.Current;
        await using var provider = ProviderWith(accessor);

        ScopedMarker? resolved = null;
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsSystem.RunInSystemScopeAsync(
                provider.GetRequiredService<IServiceScopeFactory>(),
                services =>
                {
                    resolved = services.GetRequiredService<ScopedMarker>();
                    throw new InvalidOperationException("the detached body failed");
                }));

        Assert.Equal("the detached body failed", thrown.Message);
        Assert.Same(caller, accessor.Current);
        Assert.NotNull(resolved);
        Assert.True(resolved!.Disposed);
    }
}
