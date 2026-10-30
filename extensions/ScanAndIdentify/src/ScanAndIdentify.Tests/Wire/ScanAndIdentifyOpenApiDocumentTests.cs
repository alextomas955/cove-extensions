using Cove.Plugins;
using ScanAndIdentify.Tests.TestSupport;

namespace ScanAndIdentify.Tests.Wire;

public sealed class ScanAndIdentifyOpenApiDocumentTests : ExtensionOpenApiDocumentTests
{
    protected override IApiExtension CreateExtension() => ScanAndIdentifyFixture.Create();
}
