using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
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

    protected override void ConfigureBindingServices(IServiceCollection services)
        => services.AddWhisparrSyncBindingServices();
}
