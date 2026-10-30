using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using ScanAndIdentify.Tests.TestSupport;

namespace ScanAndIdentify.Tests.Wire;

public sealed class ScanAndIdentifyOpenApiDocumentTests : ExtensionOpenApiDocumentTests
{
    protected override IApiExtension CreateExtension() => ScanAndIdentifyFixture.Create(new FakeStore());

    // Registration-time binding only. Minimal-API binding resolves an unregistered complex type as a
    // body parameter, and the options write already has one, so leaving the accessor out throws while
    // the route is being mapped. Nothing here is ever dereferenced.
    protected override void ConfigureBindingServices(IServiceCollection services)
        => services.AddSingleton<ICurrentPrincipalAccessor>(_ => null!);
}
