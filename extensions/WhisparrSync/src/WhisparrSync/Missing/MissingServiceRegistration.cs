using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Missing;

/// <summary>Where this product's catalogue derivation services are registered.</summary>
/// <remarks>
/// Held apart from the extension's own registration site so the derivation slice owns its wiring.
/// The composition root calls this and nothing about the derivation is written there.
/// </remarks>
internal static class MissingServiceRegistration
{
    /// <summary>Registers the catalogue derivation services.</summary>
    /// <remarks>
    /// Each is scoped, because each takes either the per-request database context or the provider
    /// resolved from the host's own per-request configuration.
    /// </remarks>
    internal static IServiceCollection AddMissingDerivation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IOwnedScenePort, OwnedScenePort>();
        services.AddScoped<ISceneStatusPort, SceneStatusPort>();
        services.AddScoped<ISceneExclusionPort, SceneExclusionPort>();
        services.AddScoped<MissingIdentityResolver>();
        services.AddScoped<MissingPagePlanner>();
        return services;
    }
}
