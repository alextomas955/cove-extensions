using System.Globalization;
using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Wire;

// Drives every mounted route over real HTTP and holds the committed wire document to what came back.
// The document is emitted from handler return types, so anything written in front of a handler -
// content-type negotiation, body binding - is invisible to it and was absent until something compared
// the two. This is that comparison; the sibling drift test only pins the document against itself.
//
// Probes are built from the mounted route table, so a new route is covered the day it is added.
public sealed class HttpAnswersMatchTheWireDocumentTests
{
    // The full set, so a probe reaches the handler and is answered by the route rather than its gate.
    private static readonly string[] EveryPermission =
    [
        Permissions.VideosRead, Permissions.VideosWrite,
        Permissions.ImagesRead, Permissions.ImagesWrite,
        Permissions.AudiosRead, Permissions.AudiosWrite,
        Permissions.TextsRead, Permissions.TextsWrite,
        Permissions.ExtensionsConfigure,
    ];

    // Two callers, because the refusals differ: one holding everything reaches each handler, and one
    // holding nothing reaches the refusal every handler shares. A route's own 403 is the only one this
    // seam produces - Cove's middleware, which writes the other, is not mounted here.
    [Fact]
    public async Task EveryAnswerIsDeclaredForItsRoute()
    {
        var document = await WireDocument.ReadAsync();
        var undeclared = new List<string>();

        foreach (var principal in new[]
                 {
                     FakePrincipalAccessor.WithPermissions(EveryPermission),
                     FakePrincipalAccessor.None(),
                 })
        {
            await using var host = await TransportHost.BootAsync(principal);
            Assert.NotEmpty(host.MountedRoutes);

            foreach ((string method, string pattern) in host.MountedRoutes)
            {
                foreach (var probe in ProbesFor(method))
                {
                    using var request = new HttpRequestMessage(
                        new HttpMethod(method), FillRouteValues(pattern))
                    {
                        Content = probe.Body is null
                            ? null
                            : new StringContent(probe.Body, Encoding.UTF8, probe.ContentType!),
                    };

                    var response = await host.Client.SendAsync(request);
                    var empty = (await response.Content.ReadAsByteArrayAsync()).Length == 0;
                    var mediaType = empty ? null : response.Content.Headers.ContentType?.MediaType;

                    var complaint = document.Explain(
                        method, pattern, (int)response.StatusCode, mediaType);
                    if (complaint is not null)
                    {
                        undeclared.Add($"{method} {pattern} sent {probe.Name}: {complaint}");
                    }
                }
            }
        }

        Assert.Empty(undeclared);
    }

    private sealed record Probe(string Name, string? Body, string? ContentType);

    private static IEnumerable<Probe> ProbesFor(string method)
    {
        yield return new Probe("no body", null, null);
        if (method is not ("POST" or "PUT"))
        {
            yield break;
        }

        // Binding runs in front of the handler, so a route that needs a body answers the probes below
        // without ever reaching its own refusal. This one is what reaches it.
        yield return new Probe(
            "a body every route binds",
            """{"entityType":"video","entityIds":[1]}""",
            "application/json");

        yield return new Probe("a body declared as text/plain", "{}", "text/plain");
        yield return new Probe("JSON that does not parse", "{", "application/json");
        yield return new Probe("JSON of the wrong shape", "[]", "application/json");
    }

    // Every parameterised route here takes an id it answers for whether or not that id names
    // anything, so one literal serves them all.
    private static string FillRouteValues(string pattern)
        => string.Join('/', pattern.Split('/').Select(segment => segment.StartsWith('{') ? "1" : segment));

    // The committed document, read as the generator reads it rather than re-emitted: a comparison
    // against a fresh emit would agree with the emitter by construction and say nothing about the
    // file the TypeScript types are built from.
    private sealed class WireDocument
    {
        private readonly JsonElement _paths;

        private WireDocument(JsonElement paths) => _paths = paths;

        public static async Task<WireDocument> ReadAsync()
        {
            var path = ExtensionOpenApiDocumentTests.CommittedDocumentPath(RenamerFixture.Create().Id);
            var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            return new WireDocument(json.RootElement.GetProperty("paths"));
        }

        /// Returns null when the document declares this answer, else what it is missing.
        public string? Explain(string method, string pattern, int status, string? mediaType)
        {
            if (!_paths.TryGetProperty(pattern, out var item)
                || !item.TryGetProperty(method.ToLowerInvariant(), out var operation))
            {
                return "the document describes no such operation";
            }

            var key = status.ToString(CultureInfo.InvariantCulture);
            if (!operation.GetProperty("responses").TryGetProperty(key, out var response))
            {
                return $"the server answered {key}, which the document does not declare";
            }

            if (mediaType is null)
            {
                return null;
            }

            var declared = response.TryGetProperty("content", out var content)
                ? content.EnumerateObject().Select(entry => entry.Name).ToArray()
                : [];

            if (declared.Contains(mediaType, StringComparer.Ordinal))
            {
                return null;
            }

            var alternatives = declared.Length == 0 ? "no body there" : string.Join(", ", declared);
            return $"the server answered {key} as {mediaType}, and the document declares {alternatives}";
        }
    }
}
