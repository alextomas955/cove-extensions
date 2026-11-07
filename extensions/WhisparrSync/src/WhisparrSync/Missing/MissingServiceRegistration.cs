using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Missing;

internal static class MissingServiceRegistration
{
    // Each is scoped, because each takes either the per-request database context or the provider
    // resolved from the host's own per-request configuration.
    internal static IServiceCollection AddMissingDerivation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // One slot for the last entity's catalogue, shared by every request, so the count beside a
        // tab and the page under it are one read of the instance.
        services.AddSingleton(
            provider => new InstanceCatalogueCache(
                provider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddScoped(provider => new MissingEntryAccess(
            provider.GetRequiredService<IEntityIdentityPort>(),
            provider.GetRequiredService<InstanceCatalogueCache>(),
            provider.GetRequiredService<BackgroundWork>()));
        services.AddScoped<IOwnedScenePort, OwnedScenePort>();
        services.AddScoped<IEntityNamePort, EntityNamePort>();
        services.AddScoped<MissingIdentityResolver>();
        services.AddScoped<MissingPagePlanner>();
        return services;
    }
}
