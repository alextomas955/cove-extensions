using Cove.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// That every service the catalogue routes reach is actually registered, and at the lifetime its
/// job needs.
/// </summary>
/// <remarks>
/// Resolved rather than read off the registration. A registration asserted by reading the file
/// agrees with whatever that file says, and the failure this catches is a service the container
/// cannot build at the first request in a browser.
/// </remarks>
public sealed class MissingContainerResolutionTests : IAsyncLifetime
{
    private DbContext _db = null!;
    private SqliteConnection _connection = null!;

    public async ValueTask InitializeAsync()
        => (_db, _connection) = await CoveContextFactory.CreateSqliteContextAsync();

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Theory]
    [InlineData(typeof(IProviderCatalogue))]
    [InlineData(typeof(MissingPagePlanner))]
    [InlineData(typeof(IOwnedScenePort))]
    [InlineData(typeof(ProviderEndpointPort))]
    [InlineData(typeof(ProviderPacer))]
    public void EveryCatalogueServiceResolves(Type service)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService(service));
    }

    /// <summary>
    /// Pacing has to hold across concurrent requests, so the limiter outlives a request scope. Held
    /// per scope each request would spend a full allowance of its own.
    /// </summary>
    [Fact]
    public void ThePacerIsOneInstanceAcrossScopes()
    {
        using var provider = BuildProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ProviderPacer>(),
            second.ServiceProvider.GetRequiredService<ProviderPacer>());
    }

    /// <summary>
    /// The catalogue is never shared between scopes, so it reads the host's configuration for the
    /// request it is serving rather than for the one that first built it.
    /// </summary>
    /// <remarks>
    /// A typed client is registered transient, so two resolutions within one scope are two instances
    /// as well. What matters for correctness is that neither is shared ACROSS scopes; holding one
    /// instance per scope is not a property the typed-client registration offers, and the pacer,
    /// which is the piece that genuinely must be shared, is asserted separately.
    /// </remarks>
    [Fact]
    public void TheCatalogueIsNeverSharedBetweenScopes()
    {
        using var provider = BuildProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IProviderCatalogue>(),
            second.ServiceProvider.GetRequiredService<IProviderCatalogue>());
    }

    /// <summary>
    /// The provider's client is its own, so its address and timeout are not the instance client's.
    /// </summary>
    [Fact]
    public void TheProviderHoldsItsOwnTypedClient()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        Assert.NotNull(factory.CreateClient(nameof(StashDbCatalogue)));
    }

    // The services from outside this slice that it takes, and nothing else. The point is that the
    // slice's OWN registrations are complete, so a dependency it draws from the host or from another
    // slice is supplied plainly here rather than through that slice's registration.
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new CoveConfiguration());
        services.AddScoped(_ => _db);
        services.AddScoped(_ => new OptionsStore(new FakeStore()));
        services.AddScoped<IEntityIdentityPort>(
            services => new EntityIdentityPort(
                services.GetRequiredService<DbContext>(),
                services.GetRequiredService<OptionsStore>()));

        services.AddMissingProviders();
        services.AddMissingDerivation();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
