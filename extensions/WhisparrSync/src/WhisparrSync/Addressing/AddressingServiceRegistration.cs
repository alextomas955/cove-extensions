using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Import;
using WhisparrSync.Options;

namespace WhisparrSync.Addressing;

/// <summary>Where this product's outbound path addressing is registered.</summary>
/// <remarks>
/// Held apart from the extension's own registration site so the slice owns its wiring. The
/// composition root calls this and nothing about the slice is written there.
/// </remarks>
internal static class AddressingServiceRegistration
{
    /// <summary>Registers the sample-file source, the address port and the held agreements.</summary>
    /// <remarks>
    /// The cache is a singleton: a run reads many folders under one root, and a reading held per
    /// scope would be a reading established per request. The two ports are scoped, because both
    /// reach the per-request database context or the per-request stored options.
    /// <para>
    /// The logger arrives as an argument rather than from the container, so the slice writes to this
    /// extension's own logger the way every other service it composes does.
    /// </para>
    /// </remarks>
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
