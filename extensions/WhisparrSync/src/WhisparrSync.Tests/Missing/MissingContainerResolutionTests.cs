using Cove.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;

namespace WhisparrSync.Tests.Missing;

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
    [InlineData(typeof(ProviderCatalogueSource))]
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

    // Pacing has to hold across concurrent requests. Held per scope, each request would spend a
    // full allowance of its own.
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

    // One choice per request. Transient, each reader inside a request would start the
    // stored-generation read again and two arms of one request could answer against different
    // sources. Singleton, a host reconfigured between requests would keep the source the container
    // was built with.
    [Fact]
    public void TheChoiceIsOnePerScopeAndNotSharedBetweenScopes()
    {
        using var provider = BuildProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ProviderCatalogueChoice>(),
            first.ServiceProvider.GetRequiredService<ProviderCatalogueChoice>());
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<ProviderCatalogueChoice>(),
            second.ServiceProvider.GetRequiredService<ProviderCatalogueChoice>());
    }

    // The choice a source answers with is the one its own scope holds, so a catalogue read in one
    // arm of a request is the catalogue the other arm reads from.
    [Fact]
    public async Task ASourceAnswersTheCatalogueItsOwnScopeChose()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<ProviderCatalogueSource>();

        var first = await source(TestContext.Current.CancellationToken);
        var second = await source(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
    }

    [Fact]
    public void TheProviderHoldsItsOwnTypedClient()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        Assert.NotNull(factory.CreateClient(nameof(StashDbCatalogue)));
    }

    // Only the services this slice takes from outside it, so the test proves the slice's own
    // registrations are complete rather than another slice's.
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
