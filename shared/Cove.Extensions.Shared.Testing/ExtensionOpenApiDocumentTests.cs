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
/// it differs from the committed copy, so a route or a wire shape that moves cannot land unnoticed.
/// Set <c>COVE_WIRE_DOC_UPDATE=1</c> to rewrite the committed copy after an intended change.
/// </summary>
/// <remarks>
/// Derive once per extension and supply the two hooks; the invariants that decide whether the
/// document describes anything at all live here, where a second extension inherits rather than
/// restates them. The host is never contacted: the endpoints are mounted in an in-memory
/// <see cref="WebApplication"/>, no request is sent, and the binding services are never dereferenced,
/// which is what keeps the emit runnable on a CI leg with no Cove checkout.
/// <para>
/// Nothing here configures the wire spelling. Property casing is the host's own camelCase default and
/// an enum's string form is declared on the enum type, so the document is generated under exactly the
/// options the responses ride and there is no second declaration to keep in step.
/// </para>
/// </remarks>
public abstract class ExtensionOpenApiDocumentTests
{
    // AddOpenApi() registers IOpenApiDocumentProvider KEYED by document name, and "v1" is the name it
    // adds by default; an unkeyed resolve finds nothing.
    private const string DocumentName = "v1";

    // The untransformed title is derived from the ENTRY assembly, so it reads "testhost | v1" under
    // vstest and would move the committed document the day the runner changes — a diff about the
    // toolchain on a file whose diffs are supposed to be about the wire. Pinning both fields makes the
    // info block a constant.
    private const string PinnedTitle = "cove extension wire contract";
    private const string PinnedVersion = "1.0.0";

    /// <summary>Set to <c>1</c> to rewrite the committed document instead of comparing against it.</summary>
    public const string UpdateVariable = "COVE_WIRE_DOC_UPDATE";

    /// <summary>The extension under test, built the way the host builds it (shipped manifest applied).</summary>
    protected abstract IApiExtension CreateExtension();

    /// <summary>The repo-relative path the emitted document is written to.</summary>
    protected abstract string DocumentPath { get; }

    /// <summary>
    /// Registers whatever the extension's endpoint lambdas take as non-body parameters. Minimal-API
    /// binding treats an unregistered complex type as a second body parameter and throws at
    /// registration, so these have to resolve — but the document never invokes any of them, so a
    /// factory returning null is enough and is what keeps the emit off a real database.
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

        // NumberHandling is narrowed to Strict deliberately: the Web default also accepts numbers
        // written as strings, which the generator reports as an integer-or-string union on EVERY
        // numeric field. The server only ever WRITES a JSON number, so this is exact for responses.
        // It overstates the request side, where the host does still accept a string-encoded number —
        // the document is generated to be a client-codegen input, and a client that never sends the
        // looser form is the outcome worth having.
        builder.Services.ConfigureHttpJsonOptions(
            options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

        var app = builder.Build();
        CreateExtension().MapEndpoints(app);

        // MANDATORY, and the single most dangerous line to omit here. A WebApplication's own route
        // registrations are not folded into the DI EndpointDataSource until routing middleware is built
        // at start, so without this the data source is empty, the provider still returns a perfectly
        // valid ~110-byte document with ZERO paths, and a test that only checked for non-empty JSON
        // would pass over it.
        await app.StartAsync();

        var routes = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .ToList();

        // Before anything is compared: an empty route set is a hard failure, never a pass.
        Assert.NotEmpty(routes);

        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(CancellationToken.None);

        var operations = document.Paths
            .Where(path => path.Value.Operations is not null)
            .SelectMany(path => path.Value.Operations!.Select(
                entry => (Path: path.Key, Method: entry.Key, Operation: entry.Value)))
            .ToList();

        Assert.Equal(routes.Count, operations.Count);

        // Every mounted route must also SAY what it returns. A documented operation with no response
        // content is the failure mode this whole mechanism exists to close: it reads as covered, emits a
        // valid document, and describes nothing a client could be generated from. Both counts are
        // compared against the live route table rather than against each other, and the assertion sits
        // after the non-empty check above, so an empty document can never satisfy it by having nothing
        // to disagree about. There is deliberately NO list of routes expected to be documented — an
        // allowlist is the shape that lets a gate lose a route in silence.
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

        var path = ResolveDocumentPath();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // One call, so an interrupted run leaves either the previous complete document or the new
            // one, never a half-written file that the next comparison would report as a wire change.
            File.WriteAllText(path, json);
            return;
        }

        // Compare rather than overwrite, so THIS test is the gate. Writing and leaving the check to a
        // later CI step splits the two: the run that detects the drift is not the run that produced it,
        // a developer's `dotnet test` reports success while silently dirtying the working tree, and the
        // separate step cannot tell an unchanged-because-correct document from one whose emit never ran.
        Assert.True(
            File.Exists(path),
            $"No committed wire document at {DocumentPath}. Re-run with {UpdateVariable}=1 to write it.");

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), json);
    }

    // The document is committed and CI diffs it, so its bytes must not depend on which platform emitted
    // them: a StringWriter takes its line breaks from Environment.NewLine, which would otherwise leave
    // the Windows and Linux runs each rewriting what the other committed. The non-empty precondition
    // comes first because a rewrite that read no input at all produces a clean-looking result.
    private static string NormalizeForCommit(string serialized)
    {
        Assert.NotEmpty(serialized);
        return serialized.ReplaceLineEndings("\n");
    }

    // The document path is repo-relative so the same test writes the same file from any working
    // directory and on either platform; the test assembly sits several unstable levels below the root
    // (configuration, framework), so the root is found by the catalog rather than counted out in "..".
    // Protected rather than private so a derived suite can assert ON the committed artifact — the one
    // CI diffs — instead of re-deriving the same walk and drifting from it.
    protected string ResolveDocumentPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "extensions", "catalog.json")))
            {
                return Path.Combine(directory.FullName, DocumentPath);
            }
        }

        throw new InvalidOperationException(
            $"No extensions/catalog.json above {AppContext.BaseDirectory}, so the repo-relative "
                + $"document path '{DocumentPath}' cannot be resolved.");
    }
}
