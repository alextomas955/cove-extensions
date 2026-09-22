using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Addressing;
using WhisparrSync.Connection;
using WhisparrSync.Import;
using WhisparrSync.Library;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// Minimal-API binding treats an unregistered complex type as a second body parameter and throws
// while the route is being mapped, so every non-body parameter type has to resolve here. Nothing is
// ever dereferenced, which keeps a registration-only host off a real database.
internal static class BindingServices
{
    public static IServiceCollection AddWhisparrSyncBindingServices(this IServiceCollection services)
    {
        services.AddSingleton<ICurrentPrincipalAccessor>(_ => null!);
        services.AddSingleton<IConnectionTestRunner>(_ => null!);
        services.AddSingleton<IJobService>(_ => null!);
        services.AddSingleton<IWhisparrInstanceFactory>(_ => null!);
        services.AddSingleton<IEntityIdentityPort>(_ => null!);
        services.AddSingleton<LibraryStatusPort>(_ => null!);
        services.AddSingleton<ILibraryCardIdentityPort>(_ => null!);
        services.AddSingleton<SyncPreviewCache>(_ => null!);
        services.AddSingleton<ICredentialPort>(_ => null!);
        services.AddSingleton<ICallbackSecretPort>(_ => null!);
        services.AddSingleton<IWhisparrNotificationPort>(_ => null!);
        services.AddSingleton<ProviderEndpointPort>(_ => null!);
        services.AddSingleton<MissingPagePlanner>(_ => null!);
        services.AddSingleton<OptionsStore>(_ => null!);
        services.AddSingleton<ICoveLibraryPort>(_ => null!);
        services.AddSingleton<IFolderAddressPort>(_ => null!);
        services.AddSingleton<OptionsWriteGate>(_ => null!);
        services.AddSingleton<RegistrationGate>(_ => null!);
        services.AddSingleton<TimeProvider>(_ => null!);
        return services;
    }
}
