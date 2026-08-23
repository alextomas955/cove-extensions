using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Xunit;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// Emits an extension's OpenAPI document from its own <c>MapEndpoints</c> registration and fails when
/// it differs from the committed copy. Set <c>COVE_WIRE_DOC_UPDATE=1</c> to rewrite that copy after an
/// intended change.
/// </summary>
/// <remarks>
/// Derive once per extension and supply the two hooks. The host is never contacted: the endpoints are
/// mounted in an in-memory <see cref="WebApplication"/>, no request is sent and the binding services are
/// never dereferenced, which keeps the emit runnable on a CI leg with no Cove checkout.
/// <para>
/// Nothing here configures the wire spelling. Property casing is the host's camelCase default, and an
/// enum's string form is declared on the enum type.
/// </para>
/// </remarks>
public abstract class ExtensionOpenApiDocumentTests
{
    // AddOpenApi() registers IOpenApiDocumentProvider KEYED by document name; an unkeyed resolve finds
    // nothing.
    private const string DocumentName = "v1";

    // The untransformed title is derived from the ENTRY assembly, so it would move the committed
    // document the day the test runner changes.
    private const string PinnedTitle = "cove extension wire contract";
    private const string PinnedVersion = "1.0.0";

    /// <summary>Set to <c>1</c> to rewrite the committed document instead of comparing against it.</summary>
    public const string UpdateVariable = "COVE_WIRE_DOC_UPDATE";

    // Fixed layout, not a catalog field: only the extension's own directory is declared there.
    private const string DocumentSubPath = "wire/openapi.json";

    /// <summary>The extension under test, built the way the host builds it (shipped manifest applied).</summary>
    protected abstract IApiExtension CreateExtension();

    /// <summary>
    /// Registers whatever the extension's endpoint lambdas take as non-body parameters. Minimal-API
    /// binding treats an unregistered complex type as a second body parameter and throws at
    /// registration, so these have to resolve; the document invokes none of them, so a factory
    /// returning null is enough and is what keeps the emit off a real database.
    /// </summary>
    protected virtual void ConfigureBindingServices(IServiceCollection services) { }

    [Fact]
    public async Task MatchesTheCommittedWireDocument()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        ConfigureBindingServices(builder.Services);
        builder.Services.AddRouting();
        builder.Services.AddOpenApi(DocumentName, options => options.AddDocumentTransformer(
            (document, _, _) =>
            {
                document.Info ??= new OpenApiInfo();
                document.Info.Title = PinnedTitle;
                document.Info.Version = PinnedVersion;
                return Task.CompletedTask;
            }));

        // Strict, not the Web default: the Web default also accepts numbers written as strings, which
        // the generator reports as an integer-or-string union on EVERY numeric field. The server only
        // ever writes a JSON number. This overstates the request side, where the host does still accept
        // the looser form, and a client that never sends it is the outcome worth having.
        builder.Services.ConfigureHttpJsonOptions(
            options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

        var app = builder.Build();
        var extension = CreateExtension();
        extension.MapEndpoints(app);

        // MANDATORY. A WebApplication's route registrations are not folded into the DI
        // EndpointDataSource until routing middleware is built at start, so without this the data
        // source is empty and the provider still returns a valid document with ZERO paths.
        await app.StartAsync();

        var routes = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .ToList();

        Assert.NotEmpty(routes);

        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(CancellationToken.None);

        var operations = document.Paths
            .Where(path => path.Value.Operations is not null)
            .SelectMany(path => path.Value.Operations!.Select(
                entry => (Path: path.Key, Method: entry.Key, Operation: entry.Value)))
            .ToList();

        Assert.Equal(routes.Count, operations.Count);

        // Every mounted route must also SAY what it returns. Both counts are compared against the live
        // route table, never against each other, and the Assert.NotEmpty above rules out the empty
        // document that would otherwise satisfy this by having nothing to disagree about. No allowlist
        // of documented routes: that is the shape that lets a gate lose a route in silence.
        var withoutResponseSchema = operations
            .Where(entry => entry.Operation.Responses?.Values
                .Any(response => response.Content is { Count: > 0 }) != true)
            .Select(entry => $"{entry.Method} {entry.Path}")
            .ToList();

        Assert.True(
            operations.Count - withoutResponseSchema.Count == routes.Count,
            $"{routes.Count} route(s) are mounted but only {operations.Count - withoutResponseSchema.Count} "
                + "operation(s) describe a response body. These say nothing about what they return: "
                + string.Join(", ", withoutResponseSchema));

        var writer = new StringWriter();
        document.SerializeAsV31(new OpenApiJsonWriter(writer));
        var json = NormalizeForCommit(writer.ToString());

        var (path, relativePath) = ResolveDocumentPath(extension.Id);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // One call: an interrupted run then leaves a complete document, old or new, never a
            // half-written one the next comparison would report as a wire change.
            File.WriteAllText(path, json);
            return;
        }

        // This test is the gate. Writing here and diffing in a later CI step would split the two: a
        // developer's `dotnet test` reports success while silently dirtying the working tree, and the
        // separate step cannot tell an unchanged-because-correct document from one whose emit never ran.
        Assert.True(
            File.Exists(path),
            $"No committed wire document at {relativePath}. Re-run with {UpdateVariable}=1 to write it.");

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), json);
    }

    // A StringWriter takes its line breaks from Environment.NewLine, which would otherwise leave the
    // Windows and Linux runs each rewriting what the other committed. The non-empty precondition comes
    // first because a rewrite that read no input at all produces a clean-looking result.
    private static string NormalizeForCommit(string serialized)
    {
        Assert.NotEmpty(serialized);
        return serialized.ReplaceLineEndings("\n");
    }

    // The catalog is the one place an extension's directory is written down. The walk up to it beats a
    // counted-out "..": the test assembly's depth below the repo root varies with configuration and
    // target framework.
    protected static (string Absolute, string Relative) ResolveDocumentPath(string extensionId)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var catalogPath = Path.Combine(directory.FullName, "extensions", "catalog.json");
            if (!File.Exists(catalogPath))
            {
                continue;
            }

            using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
            var entries = catalog.RootElement.GetProperty("extensions").EnumerateArray().ToList();
            foreach (var entry in entries)
            {
                if (entry.GetProperty("id").GetString() != extensionId)
                {
                    continue;
                }

                var relative = $"{entry.GetProperty("path").GetString()}/{DocumentSubPath}";
                return (Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar)), relative);
            }

            throw new InvalidOperationException(
                $"No entry in extensions/catalog.json has id '{extensionId}'; found "
                    + string.Join(", ", entries.Select(e => e.GetProperty("id").GetString())));
        }

        throw new InvalidOperationException(
            $"No extensions/catalog.json above {AppContext.BaseDirectory}, so the wire document path "
                + $"for '{extensionId}' cannot be resolved.");
    }
}
