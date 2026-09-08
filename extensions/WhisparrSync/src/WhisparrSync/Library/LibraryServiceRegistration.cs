using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Library;

/// <summary>Where this product's library card status services are registered.</summary>
/// <remarks>
/// Held apart from the extension's own registration site so the slice owns its wiring. The
/// composition root calls this and nothing about the slice is written there.
/// </remarks>
internal static class LibraryServiceRegistration
{
    /// <summary>Registers the library card status services.</summary>
    /// <remarks>
    /// Scoped, because the port takes the identity port, which takes the per-request database
    /// context.
    /// </remarks>
    internal static IServiceCollection AddLibraryStatus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ILibraryStatusPort, LibraryStatusPort>();
        return services;
    }
}
