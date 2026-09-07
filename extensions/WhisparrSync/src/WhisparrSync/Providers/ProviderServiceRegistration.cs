using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Providers;

/// <summary>Where this product's metadata provider services are registered.</summary>
/// <remarks>
/// Held apart from the extension's own registration site so the provider slice owns its wiring. The
/// composition root calls this and nothing about a provider is written there.
/// </remarks>
internal static class ProviderServiceRegistration
{
    /// <summary>Registers the metadata provider services.</summary>
    internal static IServiceCollection AddMissingProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped: the host configuration is read per request, so a source the host is reconfigured
        // to is picked up without the container being rebuilt.
        services.AddScoped(
            provider => new ProviderEndpointPort(provider.GetService<CoveConfiguration>()));

        // A singleton, because pacing has to hold across concurrent requests. Held per scope it
        // would let each request spend a full allowance.
        services.AddSingleton<ProviderPacer>();

        // Each provider carries its own timeout and handler rather than sharing the instance
        // client's. The bound is read off that client so one setting bounds one attempt everywhere.
        services
            .AddHttpClient<StashDbCatalogue>(client => client.Timeout = WhisparrClient.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(Handler)
            .AddTypedClient((client, provider) => new StashDbCatalogue(
                client,
                provider.GetRequiredService<ProviderEndpointPort>(),
                provider.GetRequiredService<OptionsStore>(),
                provider.GetRequiredService<ProviderPacer>(),
                provider.GetService<ILogger<StashDbCatalogue>>() as ILogger ?? NullLogger.Instance));

        services
            .AddHttpClient<ThePornDbCatalogue>(client =>
                client.Timeout = WhisparrClient.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(Handler)
            .AddTypedClient((client, provider) => new ThePornDbCatalogue(
                client,
                provider.GetRequiredService<ProviderEndpointPort>(),
                provider.GetRequiredService<OptionsStore>(),
                provider.GetRequiredService<ProviderPacer>(),
                provider.GetService<ILogger<ThePornDbCatalogue>>() as ILogger
                    ?? NullLogger.Instance));

        // One registration of the seam, resolving to the provider the connected generation reads
        // from. A second registration of either catalogue under this service would be shadowed by
        // this one rather than misbehaving, so it would be dead wiring a reader takes for the live
        // path.
        services.AddScoped<IProviderCatalogue>(provider => new ProviderCatalogueSelector(
            provider.GetRequiredService<OptionsStore>(),
            provider.GetRequiredService<StashDbCatalogue>(),
            provider.GetRequiredService<ThePornDbCatalogue>()));

        return services;
    }

    private static HttpClientHandler Handler()
        => new()
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = WhisparrClient.MaxRedirects,
        };
}

/// <summary>The catalogue of the provider the connected generation identifies against.</summary>
/// <remarks>
/// Which generation is connected is a stored setting, so the choice is made per scope from the
/// options rather than at container build time.
/// <para>
/// The seam's ordering and capability members carry no cancellation and no result to await, so a
/// caller reading one before any read has been asked for waits here for the load this type started
/// when it was built.
/// </para>
/// </remarks>
internal sealed class ProviderCatalogueSelector : IProviderCatalogue
{
    private readonly Task<IProviderCatalogue> _selected;

    internal ProviderCatalogueSelector(
        OptionsStore options, StashDbCatalogue stashDb, ThePornDbCatalogue thePornDb)
        => _selected = SelectAsync(options, stashDb, thePornDb);

    public IReadOnlyList<ProviderSortOption> Sorts => Selected.Sorts;

    public ProviderCapabilitySet Capabilities => Selected.Capabilities;

    private IProviderCatalogue Selected => _selected.GetAwaiter().GetResult();

    public async Task<ProviderCatalogueAnswer> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        var catalogue = await _selected.ConfigureAwait(false);
        return await catalogue.ReadPageAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<int?> ReadCatalogueSizeAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        var catalogue = await _selected.ConfigureAwait(false);
        return await catalogue.ReadCatalogueSizeAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<ProviderIdentityLookup> LookUpByNameAsync(
        WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
    {
        var catalogue = await _selected.ConfigureAwait(false);
        return await catalogue.LookUpByNameAsync(kind, name, aliases, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
        WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
    {
        var catalogue = await _selected.ConfigureAwait(false);
        return await catalogue.ListFacetMenusAsync(kind, providerEntityId, ct).ConfigureAwait(false);
    }

    private static async Task<IProviderCatalogue> SelectAsync(
        OptionsStore options, StashDbCatalogue stashDb, ThePornDbCatalogue thePornDb)
    {
        var stored = await options.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        return stored.SelectedGeneration == WhisparrGeneration.V2 ? thePornDb : stashDb;
    }
}
