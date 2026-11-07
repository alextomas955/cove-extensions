using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;

namespace WhisparrSync.Library;

internal static class LibraryServiceRegistration
{
    // Scoped, because the port takes the identity port, which takes the per-request database
    // context. The logger arrives as an argument rather than from the container, so the slice writes
    // to this extension's own logger.
    internal static IServiceCollection AddLibraryStatus(this IServiceCollection services, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped(resolved => new LibraryStatusPort(
            resolved.GetRequiredService<IEntityIdentityPort>(), log));
        services.AddScoped<ILibraryCardIdentityPort>(resolved => new LibraryCardIdentityPort(
            resolved.GetRequiredService<DbContext>(),
            resolved.GetRequiredService<OptionsStore>()));
        services.AddScoped<ILibrarySceneIdentityPort>(resolved => new LibrarySceneIdentityPort(
            resolved.GetRequiredService<DbContext>(),
            resolved.GetRequiredService<OptionsStore>()));
        return services;
    }
}
