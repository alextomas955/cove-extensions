using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Contracts;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Wire;

/// <summary>
/// Emits Renamer's wire document from its shipped registration into
/// <c>extensions/Renamer/wire/openapi.json</c>, which CI then diffs. L2 by this repo's taxonomy — the
/// endpoints are mounted in a real in-process host — though the emit sends no request.
/// </summary>
[Trait("Tier", "L2")]
public sealed class RenamerOpenApiDocumentTests : ExtensionOpenApiDocumentTests
{
    protected override IApiExtension CreateExtension()
    {
        var extension = RenamerFixture.Create();
        ((IStatefulExtension)extension).SetStore(new FakeStore());
        return extension;
    }

    protected override JsonSerializerOptions ResponseOptions() =>
        PreviewContracts.PreviewResponseJsonOptions;

    protected override string DocumentPath => "extensions/Renamer/wire/openapi.json";

    // Registration-time binding only. Minimal-API binding resolves an unregistered complex type as a
    // body parameter, and /preview already has one, so leaving DbContext out throws while the route is
    // being mapped. Nothing here is ever dereferenced — which is what keeps the emit off CoveContext and
    // therefore on the CI leg that has no cove checkout.
    protected override void ConfigureBindingServices(IServiceCollection services)
    {
        services.AddSingleton<DbContext>(_ => null!);
        services.AddSingleton<ICurrentPrincipalAccessor>(_ => null!);
        services.AddSingleton<IJobService>(_ => null!);
    }
}
