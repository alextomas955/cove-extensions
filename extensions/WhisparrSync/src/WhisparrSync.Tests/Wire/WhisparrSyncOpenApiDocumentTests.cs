using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Connection;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Wire;

/// <summary>
/// Emits Whisparr Sync's wire document from its shipped registration and fails when it differs from
/// the committed copy. The endpoints are mounted in a real in-process host, though the emit sends no
/// request.
/// </summary>
public sealed class WhisparrSyncOpenApiDocumentTests : ExtensionOpenApiDocumentTests
{
    protected override IApiExtension CreateExtension() => WhisparrSyncFixture.Create();

    // Registration-time binding only. Every non-body parameter a handler takes has to resolve, because
    // minimal-API binding treats an unregistered complex type as a second body parameter and throws
    // while the route is being mapped. Nothing here is ever dereferenced: the document is emitted from
    // the registration and sends no request.
    protected override void ConfigureBindingServices(IServiceCollection services)
    {
        services.AddSingleton<ICurrentPrincipalAccessor>(_ => null!);
        services.AddSingleton<IWhisparrConnectionTester>(_ => null!);
        services.AddSingleton<ICredentialPort>(_ => null!);
        services.AddSingleton<OptionsStore>(_ => null!);
        services.AddSingleton<TimeProvider>(_ => null!);
    }
}
