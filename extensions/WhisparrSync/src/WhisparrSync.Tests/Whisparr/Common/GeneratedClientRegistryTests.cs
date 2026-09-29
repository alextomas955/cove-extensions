using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// The gateways reach this registry once per request and dispose it with the container. What they
// cannot stage is a reach caught part way through registering, because the handler a gateway hands
// the registration is not asked for until a request resolves an api. Taken here against the
// registry, where registering is the test's own callback and can be held open.
public sealed class GeneratedClientRegistryTests
{
    // Long enough that a loaded machine does not report a wait as a hang, short enough that a
    // genuine hang ends the run rather than blocking it.
    private static readonly TimeSpan Held = TimeSpan.FromSeconds(30);

    private static readonly Whisparr3Target SomePair =
        new(new Uri("http://whisparr:6969"), "0123456789abcdef0123456789abcdef");

    // A reach that read an undisposed registry and is still registering when disposal starts. The
    // provider it goes on to build is discarded once the request holding it releases it, so nothing
    // outlives the gateway. The reach may also be refused outright, which leaves nothing to
    // discard.
    [Fact]
    public async Task AReachStillRegisteringWhenDisposalStartsLeavesNoProviderBehind()
    {
        using var registering = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        ProviderProbe? built = null;
        var registry = new GeneratedClientRegistry<Whisparr3Target>(_ =>
        {
            registering.Set();
            released.Wait(Held);

            var services = new ServiceCollection();
            services.AddSingleton<ProviderProbe>();
            var provider = services.BuildServiceProvider();
            built = provider.GetRequiredService<ProviderProbe>();
            return provider;
        });

        GeneratedClientRegistry<Whisparr3Target>.Lease? reached = null;
        ObjectDisposedException? refused = null;
        var reaching = Task.Run(() =>
        {
            try
            {
                reached = registry.Reach(SomePair);
            }
            catch (ObjectDisposedException caught)
            {
                refused = caught;
            }
        });

        Assert.True(registering.Wait(Held), "the registration never started");
        var disposing = Task.Run(registry.Dispose);
        released.Set();
        await Task.WhenAll(reaching, disposing);

        if (refused is not null)
        {
            Assert.True(built is null or { Disposed: true }, "a refused reach left a provider open");
            return;
        }

        Assert.NotNull(reached);
        reached.Dispose();
        Assert.NotNull(built);
        Assert.True(built.Disposed, "the provider the reach built was never discarded");
    }

    // Registered by type rather than as an instance, so the provider is the one that built it and
    // therefore the one that disposes it. Its disposal is the provider's, observed from outside.
    private sealed class ProviderProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
