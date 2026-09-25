using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Linking;

internal static class LinkingServiceRegistration
{
    // The port is a singleton because it holds nothing: it reaches the filesystem on each call and
    // keeps no per-request state, so a scoped one would be a new object per delivery answering
    // exactly as the last did.
    internal static IServiceCollection AddTreeLinking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ITreeLinkPort>(_ => new TreeLinkPort());
        return services;
    }
}
