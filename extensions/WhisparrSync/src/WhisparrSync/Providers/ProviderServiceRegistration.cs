using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        // Its own typed client, so the provider carries its own timeout and handler rather than
        // sharing the instance client's. The bound is read off that client so one setting bounds one
        // attempt everywhere.
        services
            .AddHttpClient<IProviderCatalogue, StashDbCatalogue>(client =>
                client.Timeout = WhisparrClient.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(
                () => new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = WhisparrClient.MaxRedirects,
                })
            .AddTypedClient<IProviderCatalogue>((client, provider) => new StashDbCatalogue(
                client,
                provider.GetRequiredService<ProviderEndpointPort>(),
                provider.GetRequiredService<OptionsStore>(),
                provider.GetRequiredService<ProviderPacer>(),
                provider.GetService<ILogger<StashDbCatalogue>>() as ILogger
                    ?? NullLogger.Instance));

        return services;
    }
}
