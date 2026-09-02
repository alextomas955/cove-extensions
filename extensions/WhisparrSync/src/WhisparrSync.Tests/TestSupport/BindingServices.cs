using Cove.Core.Auth;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Connection;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Whatever this extension's endpoint lambdas take as non-body parameters.
/// </summary>
/// <remarks>
/// Registration-time binding only. Minimal-API binding treats an unregistered complex type as a second
/// body parameter and throws while the route is being mapped, so each of these has to resolve; nothing
/// here is ever dereferenced, which is what keeps a registration-only host off a real database.
/// <para>
/// Declared once because two tests mount the same registration for different reasons, and a second copy
/// is a second chance for one of them to fall behind a handler that grew a parameter.
/// </para>
/// </remarks>
internal static class BindingServices
{
    public static IServiceCollection AddWhisparrSyncBindingServices(this IServiceCollection services)
    {
        services.AddSingleton<ICurrentPrincipalAccessor>(_ => null!);
        services.AddSingleton<IConnectionTestRunner>(_ => null!);
        services.AddSingleton<ICredentialPort>(_ => null!);
        services.AddSingleton<ICallbackSecretPort>(_ => null!);
        services.AddSingleton<IWhisparrNotificationPort>(_ => null!);
        services.AddSingleton<OptionsStore>(_ => null!);
        services.AddSingleton<OptionsWriteGate>(_ => null!);
        services.AddSingleton<RegistrationGate>(_ => null!);
        services.AddSingleton<TimeProvider>(_ => null!);
        return services;
    }
}
