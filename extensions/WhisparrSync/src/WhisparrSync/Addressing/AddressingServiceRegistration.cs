using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Import;
using WhisparrSync.Options;

namespace WhisparrSync.Addressing;

internal static class AddressingServiceRegistration
{
    // The cache is a singleton so one reading serves every folder under a root; a scoped cache
    // would re-establish per request. The two ports are scoped because they reach the per-request
    // database context and stored options.
    internal static IServiceCollection AddFolderAddressing(
        this IServiceCollection services, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(
            resolved => new FolderAgreementCache(resolved.GetRequiredService<TimeProvider>()));
        services.AddScoped<ISampleFilePort>(
            resolved => new SampleFilePort(resolved.GetRequiredService<DbContext>()));
        services.AddScoped<IFolderAddressPort>(resolved => new FolderAddressPort(
            resolved.GetRequiredService<ISampleFilePort>(),
            resolved.GetRequiredService<ICoveLibraryPort>(),
            resolved.GetRequiredService<IReportedRootPort>(),
            resolved.GetRequiredService<OptionsStore>(),
            resolved.GetRequiredService<FolderAgreementCache>(),
            log));
        return services;
    }
}
