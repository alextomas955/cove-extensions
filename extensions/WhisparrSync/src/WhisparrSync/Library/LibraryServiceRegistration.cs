using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;

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
    /// <para>
    /// The logger arrives as an argument rather than from the container, so the slice writes to this
    /// extension's own logger the way every other service it composes does.
    /// </para>
    /// </remarks>
    internal static IServiceCollection AddLibraryStatus(this IServiceCollection services, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ILibraryStatusPort>(resolved => new LibraryStatusPort(
            resolved.GetRequiredService<IEntityIdentityPort>(), log));
        services.AddScoped<ILibraryCardIdentityPort>(resolved => new LibraryCardIdentityPort(
            resolved.GetRequiredService<DbContext>(),
            resolved.GetRequiredService<OptionsStore>()));
        return services;
    }
}
