using System.Net;
using System.Text.Json;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Overlays the per-entity catalogue read onto a fixture: the API description declaring the two narrow routes,
/// the sibling read answering the ids of a supplied movie array, the by-id hydration answering exactly the rows
/// it is asked for, and the entity existence probe.
/// </summary>
/// <remarks>
/// It exists so a fixture written as an ordered sequence keeps its remaining steps and their order: the
/// per-entity read is served OUT of the sequence, which is what lets a test about import or registration
/// behaviour stay about that rather than about how many calls precede it.
/// </remarks>
internal static class PerEntityCatalogueFixture
{
    private const string DeclaringDocument =
        "{\"paths\":{\"/api/v3/movie/listbystudioforeignid\":{},\"/api/v3/movie/listbyperformerforeignid\":{}}}";

    /// <param name="handler">The fixture to overlay.</param>
    /// <param name="moviesJson">The entity's catalogue as a JSON movie array — the rows the whole-set read used to return.</param>
    /// <param name="entityKnown">Whether the existence probe answers 200. Only consulted when the catalogue is empty.</param>
    internal static FakeHttpMessageHandler ServingCatalogue(
        this FakeHttpMessageHandler handler, string moviesJson, bool entityKnown = true)
    {
        using var parsed = JsonDocument.Parse(moviesJson);
        var rowsById = parsed.RootElement.EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetInt32(), row => row.GetRawText());

        return handler.Also(request =>
        {
            if (request.Url.EndsWith("/docs/v3/openapi.json", StringComparison.Ordinal))
            {
                return Ok(DeclaringDocument);
            }

            if (request.Url.Contains("/movie/listby", StringComparison.OrdinalIgnoreCase))
            {
                return Ok(JsonSerializer.Serialize(rowsById.Keys));
            }

            if (request.Url.Contains("/api/v3/studio/", StringComparison.Ordinal)
                || request.Url.Contains("/api/v3/performer/", StringComparison.Ordinal))
            {
                return entityKnown
                    ? Ok("{\"id\":7,\"foreignId\":\"entity\",\"title\":\"Entity\"}")
                    : FakeHttpMessageHandler.Respond(HttpStatusCode.NotFound, "application/json", "{}")();
            }

            if (request.Url.EndsWith("/api/v3/movie/bulk", StringComparison.Ordinal))
            {
                var asked = JsonSerializer.Deserialize<int[]>(request.Body!)!;
                return Ok($"[{string.Join(",", asked.Where(rowsById.ContainsKey).Select(id => rowsById[id]))}]");
            }

            return null;
        });
    }

    private static HttpResponseMessage Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body)();
}
