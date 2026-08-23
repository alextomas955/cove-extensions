using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Wire;

/// <summary>
/// Emits Renamer's wire document from its shipped registration and fails when it differs from the
/// committed copy. L2 by this repo's taxonomy — the endpoints are mounted in a real in-process host —
/// though the emit sends no request.
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
        var (path, _) = ResolveDocumentPath(CreateExtension().Id);
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

        // Transcribed by hand from the emitted document rather than derived from the record: a pin
        // computed from the type it guards agrees with it forever. Each problem channel is a COUNT
        // beside a bounded SAMPLE, and it is the count whose loss is the silent half — a generated type
        // missing `failedCount` still compiles at every site, and the panel would then state a sample's
        // length as the number of problems a user is deciding about.
        var undo = PropertyNames(schemas, "UndoResult");
        Assert.Contains("undone", undo);
        Assert.Contains("failedCount", undo);
        Assert.Contains("failedSample", undo);
        Assert.Contains("skippedCount", undo);
        Assert.Contains("skippedSample", undo);
        Assert.Contains("warningCount", undo);
        Assert.Contains("warningSample", undo);

        // The retired per-entry arrays. They were bounded only by the batch, and a batch reaches library
        // size — the same reason the summary above carries no collection.
        Assert.DoesNotContain("failed", undo);
        Assert.DoesNotContain("skipped", undo);
        Assert.DoesNotContain("warnings", undo);

        // A count emitted as an array is the one shape that would satisfy the names above while
        // restoring the unbounded payload, so the counts are pinned as scalars.
        foreach (var name in new[] { "undone", "failedCount", "skippedCount", "warningCount" })
        {
            Assert.Equal(
                "integer",
                schemas.GetProperty("UndoResult").GetProperty("properties")
                    .GetProperty(name).GetProperty("type").GetString());
        }

        // The retired file-count ceiling. Its absence is the half a diff would let through quietly:
        // the frontend reads `summary.undoable` as undefined, which is falsy, so a stale consumer
        // would show the "cannot be reversed" banner on EVERY batch rather than none.
        Assert.DoesNotContain("undoable", PropertyNames(schemas, "PreviewSummary"));
    }
}
