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
    // AddOpenApi() registers IOpenApiDocumentProvider keyed by document name; an unkeyed resolve finds
    // nothing.
    private const string DocumentName = "v1";

    // The untransformed title is derived from the entry assembly, so it would move the committed
    // document the day the test runner changes.
    private const string PinnedTitle = "cove extension wire contract";
    private const string PinnedVersion = "1.0.0";

    private const string DocumentDescription =
        "The HTTP surface one Cove extension adds to its host. Every route is mounted under "
        + "/api/extensions/<extension id> and is reached same-origin from the host page.";

    private const string BearerScheme = "coveBearer";

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
        builder.Services.AddOpenApi(DocumentName, options =>
        {
            // What Cove's authorization middleware and ASP.NET Core's body binding write in front of
            // the handler, which the handler's declared return type cannot express.
            options.AddOperationTransformer((operation, operationContext, _) =>
            {
                HostWrittenResponses.Apply(operation, operationContext);
                return Task.CompletedTask;
            });
            options.AddDocumentTransformer(async (document, documentContext, cancellationToken) =>
            {
                await HostWrittenResponses
                    .RegisterSchemasAsync(document, documentContext, cancellationToken)
                    .ConfigureAwait(false);

                document.Info ??= new OpenApiInfo();
                document.Info.Title = PinnedTitle;
                document.Info.Version = PinnedVersion;
                document.Info.Description = DocumentDescription;

                // Relative, because an extension's routes are only ever reached same-origin: the host
                // page serves them, and its own fetch refuses any other origin. A deployment host name
                // here would be one this document cannot know.
                document.Servers = [new OpenApiServer { Url = "/", Description = "The Cove host." }];

                // Nothing in the paths says how a caller authenticates, which leaves a reader and a
                // generated client to read every 403 as arbitrary. The host attaches the bearer token;
                // an extension never sees or issues one.
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(
                    StringComparer.Ordinal);
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description =
                        "Issued by the Cove host and attached by its own authenticated fetch. "
                        + "Required only where the host has authentication turned on, which is not "
                        + "its default; where it is on, a caller without one is refused by the host "
                        + "before the extension is reached.",
                };
                // Two alternatives, because the host's own authentication is a setting and is off by
                // default. Listing only the bearer would declare a credential mandatory on a
                // deployment that accepts none; listing only the empty requirement would tell a
                // generated client it never needs one. Either is accepted, which is what is true.
                document.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, document)] = [],
                    },
                    [],
                ];
            });
        });

        // Strict, not the Web default: the Web default also accepts numbers written as strings, which
        // the generator reports as an integer-or-string union on every numeric field. The server only
        // ever writes a JSON number. It overstates the request side, where the host still accepts the
        // looser form.
        builder.Services.ConfigureHttpJsonOptions(
            options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

        var app = builder.Build();
        var extension = CreateExtension();
        extension.MapEndpoints(app);

        // A WebApplication's route registrations are not folded into the DI EndpointDataSource until
        // routing middleware is built at start, so without this the data source is empty and the
        // provider still returns a valid document with zero paths.
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

        // Every mounted route must also say what it returns. The Assert.NotEmpty above rules out the
        // empty document that would otherwise satisfy this by having nothing to disagree about.
        var withoutResponseSchema = operations
            .Where(entry => entry.Operation.Responses?.Values
                .Any(response => response.Content is { Count: > 0 }) != true)
            .Select(entry => $"{entry.Method} {entry.Path}")
            .ToList();

        Assert.Empty(withoutResponseSchema);

        // Both alternatives, by name. An empty requirement object is valid OpenAPI and means the
        // routes are reachable anonymously, which is one of the two arms here - and is also what an
        // unresolvable scheme reference serializes to. A reference carries whatever id it was built
        // with, so the id alone cannot tell those apart; it is checked against the schemes the
        // document actually declares.
        var security = document.Security ?? [];
        Assert.Equal(2, security.Count);
        var referenced = security[0].Keys.Select(key => key.Reference?.Id).ToList();
        Assert.Equal([BearerScheme], referenced);
        var declared = document.Components?.SecuritySchemes?.Keys ?? [];
        Assert.All(referenced, id => Assert.Contains(id, declared, StringComparer.Ordinal));
        Assert.Empty(security[1]);

        var writer = new StringWriter();
        document.SerializeAsV31(new OpenApiJsonWriter(writer));
        var json = NormalizeForCommit(writer.ToString());

        var (path, relativePath) = ResolveDocumentPath(extension.Id);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // One call: an interrupted run then leaves a complete document, old or new, never a
            // half-written one the next comparison would report as a wire change.
            await File.WriteAllTextAsync(path, json);
            return;
        }

        Assert.True(
            File.Exists(path),
            $"No committed wire document at {relativePath}. Re-run with {UpdateVariable}=1 to write it.");

        Assert.Equal((await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n"), json);
    }

    // A StringWriter takes its line breaks from Environment.NewLine, which would otherwise leave the
    // Windows and Linux runs each rewriting what the other committed. The non-empty precondition comes
    // first because a rewrite that read no input produces a clean-looking result.
    private static string NormalizeForCommit(string serialized)
    {
        Assert.NotEmpty(serialized);

        // The second replace works inside the JSON strings rather than between them. A schema
        // description taken from a multi-line /// comment carries the source file's own line ending,
        // escaped, where the first replace never sees it; left alone, the Windows and Linux runs each
        // rewrite a document neither of them changed.
        return serialized.ReplaceLineEndings("\n").Replace("\\r\\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>The committed wire document's path for <paramref name="extensionId"/>.</summary>
    /// <remarks>
    /// Exposed so a test can assert a live response against the contract the TypeScript types are
    /// generated from, rather than against the C# type both sides already derive from.
    /// </remarks>
    public static string CommittedDocumentPath(string extensionId) => ResolveDocumentPath(extensionId).Absolute;

    // The catalog is the one place an extension's directory is written down, and it is walked up to
    // rather than reached by a counted-out "..": the test assembly's depth below the repo root varies
    // with configuration and target framework.
    private static (string Absolute, string Relative) ResolveDocumentPath(string extensionId)
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
