using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Providers;

internal static class ProviderServiceRegistration
{
    internal static IServiceCollection AddMissingProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped: the host configuration is read per request, so a source the host is reconfigured
        // to is picked up without the container being rebuilt.
        services.AddScoped(
            provider => new ProviderEndpointPort(provider.GetService<CoveConfiguration>()));

        // Singleton: pacing has to hold across concurrent requests.
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

        // One registration of the choice. The seam itself is not registered: a catalogue is
        // asked for by the source below rather than injected, so no call site can hold one before
        // the stored choice has been read.
        services.AddScoped<ProviderCatalogueChoice>();
        services.AddScoped<ProviderCatalogueSource>(
            provider => provider.GetRequiredService<ProviderCatalogueChoice>().ChooseAsync);

        return services;
    }

    private static HttpClientHandler Handler()
        => new()
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = WhisparrClient.MaxRedirects,
        };
}
