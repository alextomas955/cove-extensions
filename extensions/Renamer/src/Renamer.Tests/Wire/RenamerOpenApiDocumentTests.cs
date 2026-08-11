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

    /// <summary>
    /// Names, on the committed artifact, the two wire shapes the undo panel is built from.
    /// </summary>
    /// <remarks>
    /// The base emits the whole document and CI diffs it, which catches ANY change but says nothing
    /// about which change was intended. These are the fields whose absence is silent: a generated
    /// TypeScript type that lost a property still compiles at every site that never reads it, and the
    /// panel's remaining figure would simply render as nothing. Stated here because this suite is one
    /// of the few that survives the bare continuous-integration leg, which is where this repository's
    /// coverage is thinnest and therefore where a gate is worth the most.
    /// </remarks>
    [Fact]
    public void TheCommittedDocumentDescribesTheUndoPanelsWireShapes()
    {
        var path = ResolveDocumentPath();
        Assert.True(File.Exists(path), $"No committed wire document at {path}.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        static IReadOnlyList<string> PropertyNames(JsonElement schemas, string schemaName)
        {
            Assert.True(
                schemas.TryGetProperty(schemaName, out var schema),
                $"The committed document has no {schemaName} schema, so the generated frontend type "
                    + "for it does not exist either.");
            return [.. schema.GetProperty("properties").EnumerateObject().Select(p => p.Name)];
        }

        var summary = PropertyNames(schemas, "LastBatchSummary");
        Assert.Contains("remainingCount", summary);
        Assert.Contains("unrestorableCount", summary);

        // The panel reads an aggregate, never the rows: a batch reaches library size, so a collection
        // here would be an unbounded payload AND a widening of what the endpoint's coarse
        // any-renamer-read gate discloses.
        Assert.All(
            schemas.GetProperty("LastBatchSummary").GetProperty("properties").EnumerateObject(),
            p => Assert.NotEqual("array", p.Value.TryGetProperty("type", out var t) ? t.GetString() : null));

        Assert.Contains("warnings", PropertyNames(schemas, "UndoResult"));

        // The retired file-count ceiling. Its absence is the half a diff would let through quietly:
        // the frontend reads `summary.undoable` as undefined, which is falsy, so a stale consumer
        // would show the "cannot be reversed" banner on EVERY batch rather than none.
        Assert.DoesNotContain("undoable", PropertyNames(schemas, "PreviewSummary"));
    }
}
