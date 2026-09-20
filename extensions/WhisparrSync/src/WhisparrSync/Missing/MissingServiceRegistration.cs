using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Missing;

internal static class MissingServiceRegistration
{
    // Each is scoped, because each takes either the per-request database context or the provider
    // resolved from the host's own per-request configuration.
    internal static IServiceCollection AddMissingDerivation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IOwnedScenePort, OwnedScenePort>();
        services.AddScoped<IEntityNamePort, EntityNamePort>();
        services.AddScoped<MissingIdentityResolver>();
        services.AddScoped<MissingPagePlanner>();
        return services;
    }
}
