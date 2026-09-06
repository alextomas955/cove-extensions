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
    /// Nothing is registered while the slice declares contracts and no implementation of them. A
    /// registration ahead of an implementation would resolve to a type that does not exist.
    /// </remarks>
    internal static IServiceCollection AddMissingDerivation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
