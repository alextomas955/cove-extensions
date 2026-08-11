using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Xunit;
using Xunit.Sdk;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// Emits an extension's OpenAPI document from its own <c>MapEndpoints</c> registration and writes it
/// to a committed file, so a route or a wire shape that moves shows up as a diff on that file.
/// </summary>
/// <remarks>
/// Derive once per extension and supply the four hooks; the invariants that decide whether the
/// document describes anything at all live here, where a second extension inherits rather than
/// restates them. The host is never contacted: the endpoints are mounted in an in-memory
/// <see cref="WebApplication"/>, no request is sent, and the binding services are never dereferenced,
/// which is what keeps the emit runnable on a CI leg with no Cove checkout.
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

    /// <summary>The extension under test, built the way the host builds it (shipped manifest applied).</summary>
    protected abstract IApiExtension CreateExtension();

    /// <summary>
    /// The serializer options the extension's own responses ride, which decide the document's property
    /// casing and enum spelling. Returns the product's instance; this class copies from it.
    /// </summary>
    protected abstract JsonSerializerOptions ResponseOptions();

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
    public async Task EmitsTheCurrentWireDocument()
    {
        var responseOptions = ResponseOptions();
        Assert.NotEmpty(responseOptions.Converters);

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

        // The schema generator reads ONE document-wide options object — the one ConfigureHttpJsonOptions
        // fills — so seeding it from the extension's own response options is what makes the document's
        // casing and enum spelling a consequence of the product rather than a second declaration of it.
        // JsonSerializerOptions is frozen after first use, so the members are copied across and the
        // instance is never assigned. NumberHandling is narrowed to Strict deliberately: the Web default
        // also accepts numbers written as strings, which the generator reports as an integer-or-string
        // union on EVERY numeric field, while the server only ever writes a JSON number.
        var copiedConverters = 0;
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = responseOptions.PropertyNamingPolicy;
            foreach (var converter in responseOptions.Converters)
            {
                options.SerializerOptions.Converters.Add(converter);
                copiedConverters++;
            }

            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });

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

        // Before any write: an empty route set is a hard failure, never a pass.
        Assert.NotEmpty(routes);

        // Proves the seeding callback actually ran. It is resolved lazily through DI, so a mis-wired
        // registration would leave the document generated from host defaults with nothing to say so.
        Assert.Equal(responseOptions.Converters.Count, copiedConverters);

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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // One call, so an interrupted or concurrent run leaves either the previous complete document or
        // the new one, never a half-written file that the CI diff would report as a wire change.
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Measures — rather than asserting from the current document's contents — that a C# doc comment
    /// reaches a schema description, which is the input the carriage-return rewrite exists for.
    /// </summary>
    /// <remarks>
    /// The comment cache the OpenAPI integration's source generator builds is scoped to the compilation
    /// that calls <c>AddOpenApi</c>, and that call is in this assembly, so the fixture shape has to be
    /// declared beside it for the description path to be reachable at all: an extension's own wire types
    /// are in a project this one cannot reference and are therefore invisible to the generator. That is
    /// why an extension's emitted document carries no schema description, and why concluding from its
    /// absence that the escape form cannot occur would be wrong — the mechanism is live at this call
    /// site, and one shape declared here is enough to put a platform-dependent newline in the output.
    /// </remarks>
    [Fact]
    public async Task ADocCommentSpanningMoreThanOneLineReachesASchemaDescription()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddOpenApi(DocumentName, _ => { });

        var app = builder.Build();
        app.MapGet("/doc-comment-fixture", () => TypedResults.Ok(new MultiLineSummaryFixture(1)));
        await app.StartAsync();

        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(CancellationToken.None);
        var writer = new StringWriter();
        document.SerializeAsV31(new OpenApiJsonWriter(writer));
        var raw = writer.ToString();

        // The description is in the document at all — the half of this that had never been checked.
        Assert.Contains("A fixture shape whose summary spans", raw, StringComparison.Ordinal);

        // And it spans lines, so the string value carries whichever newline the generated source did.
        // Matching the ESCAPE with the carriage return OPTIONAL is what keeps this platform-blind: the
        // writer escapes a line ending inside a string value either way, and which one arrives is the
        // platform's choice rather than this repository's.
        Assert.Matches(@"A fixture shape whose summary spans(\\r)?\\n", raw);

        Assert.DoesNotContain("\\r", NormalizeForCommit(raw), StringComparison.Ordinal);
    }

    /// <summary>Refuses a serialization with nothing in it, so the rewrite cannot pass over no input.</summary>
    [Fact]
    public void NormalizingRefusesADocumentWithNothingInIt() =>
        Assert.ThrowsAny<XunitException>(() => NormalizeForCommit(string.Empty));

    /// <summary>The escape-form rewrite, on the shape a multi-line description produces on Windows.</summary>
    [Fact]
    public void NormalizingRewritesACarriageReturnEscapedInsideAStringValue() =>
        Assert.Equal(
            "{\"description\":\"first\\nsecond\"}",
            NormalizeForCommit("{\"description\":\"first\\r\\nsecond\"}"));

    /// <summary>
    /// A lone escaped carriage return is not a line-ending pair, so the rewrite leaves it and the
    /// assertion is the only thing standing between it and the committed file.
    /// </summary>
    [Fact]
    public void NormalizingRefusesACarriageReturnItCannotRewrite() =>
        Assert.ThrowsAny<XunitException>(
            () => NormalizeForCommit("{\"description\":\"first\\rsecond\"}"));

    // The document is committed and CI diffs it, so its bytes must not depend on which platform emitted
    // them. Two forms of carriage return reach the serialized string and only one is a line ending the
    // CLR recognizes:
    //
    //   * the writer's own line breaks, which a StringWriter takes from Environment.NewLine, and which
    //     ReplaceLineEndings sees;
    //   * a carriage return INSIDE a string value, which the JSON writer emits as the two-character
    //     escape that ReplaceLineEndings therefore cannot see. Schema descriptions are where these come
    //     from: the OpenAPI integration's source generator folds a C# /// comment into a description by
    //     embedding it in a generated source file, and Roslyn writes that file with the platform's
    //     newline — so a summary spanning more than one line arrives as CRLF on Windows and LF on Linux
    //     even in a repository whose hand-written sources are LF everywhere.
    //
    // Both are normalized and the assertion then refuses to hand back a document still holding a
    // carriage return in the escape form, which the rewrite above deliberately does not cover for a lone
    // CR. Without all of this the Windows and Linux runs disagree about the committed file forever, each
    // rewriting what the other committed. The non-empty precondition comes first because an assertion
    // about what a string does not contain is satisfied by a string containing nothing.
    private static string NormalizeForCommit(string serialized)
    {
        Assert.NotEmpty(serialized);

        var json = serialized.ReplaceLineEndings("\n").Replace("\\r\\n", "\\n", StringComparison.Ordinal);
        Assert.DoesNotContain("\\r", json, StringComparison.Ordinal);
        return json;
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

/// <summary>
/// A fixture shape whose summary spans
/// more than one source line on purpose, so the description folded into the emitted document carries a
/// newline and the escape form the normalization rewrites is produced rather than described.
/// </summary>
/// <remarks>
/// Declared in THIS assembly because the generator's comment cache is scoped to the compilation holding
/// the <c>AddOpenApi</c> call, so a shape declared anywhere else reaches no description. It is a fixture
/// and nothing ships it: it is never part of any extension's document, which is emitted from that
/// extension's own registration.
/// </remarks>
/// <param name="Value">Present only so the shape has a member and becomes a component schema.</param>
public sealed record MultiLineSummaryFixture(int Value);
