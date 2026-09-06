using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Providers;

/// <summary>Where this product's metadata provider services are registered.</summary>
/// <remarks>
/// Held apart from the extension's own registration site so the provider slice owns its wiring. The
/// composition root calls this and nothing about a provider is written there.
/// </remarks>
internal static class ProviderServiceRegistration
{
    /// <summary>Registers the metadata provider services.</summary>
    /// <remarks>
    /// Nothing is registered while the slice declares a seam and no implementation of it. A
    /// registration ahead of an implementation would resolve to a type that does not exist.
    /// </remarks>
    internal static IServiceCollection AddMissingProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
