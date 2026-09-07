using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Push;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Push;

/// <summary>
/// The one path in this extension that can mutate a user's Whisparr instance at scale: registering every scene
/// an entity is missing.
/// </summary>
/// <remarks>
/// An entity Whisparr does not know and an entity it knows that holds nothing look identical in a row set —
/// both are empty. Collapsing them makes every scene Cove owns under the entity read as missing, and this pass
/// then registers the lot. The assertion is therefore the COUNTED number of outbound writes, never the absence
/// of an exception: a run that silently registered a thousand scenes throws nothing.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class AddAllMissingUnknownEntityTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const int CoveStudioId = 42;
    private const string UnknownStudioId = "a0000000-0000-4000-8000-0000000000ff";
    private const string KnownStudioId = "a0000000-0000-4000-8000-000000000001";

    private static WhisparrOptions Options() => new()
    {
        SelectedVersion = "v3",
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
    };

    private static HttpResponseMessage Json(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body)();

    // A v3 instance declaring the narrow routes, answering the sibling with no ids, and answering the entity
    // existence probe with `existence`. Everything else answers an empty array or an empty object, so an add
    // that DID go out succeeds and is counted rather than failing for an unrelated reason.
    private static FakeHttpMessageHandler Instance(HttpStatusCode existence)
        => FakeHttpMessageHandler.Json("[]").Also(request =>
        {
            if (request.Url.EndsWith("/docs/v3/openapi.json", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new
                {
                    paths = new Dictionary<string, object>
                    {
                        ["/api/v3/movie/listbystudioforeignid"] = new { },
                        ["/api/v3/movie/listbyperformerforeignid"] = new { },
                    },
                }));
            }

            if (request.Url.Contains("/movie/listby", StringComparison.OrdinalIgnoreCase))
            {
                return Json("[]");
            }

            if (request.Url.Contains("/api/v3/studio/", StringComparison.Ordinal))
            {
                return existence == HttpStatusCode.OK
                    ? Json(JsonSerializer.Serialize(new { id = 7, foreignId = KnownStudioId, title = "Studio Aurora" }))
                    : FakeHttpMessageHandler.Respond(existence, "application/json", "{\"message\":\"NotFound\"}")();
            }

            if (request.Url.EndsWith("/api/v3/tag", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return Json(JsonSerializer.Serialize(new { id = 1, label = "cove-sync" }));
            }

            if (request.Url.EndsWith("/api/v3/rootfolder", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new[] { new { id = 1, path = "/data/media", accessible = true, freeSpace = 1L } }));
            }

            if (request.Url.EndsWith("/api/v3/qualityprofile", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new[] { new { id = 1, name = "Any" } }));
            }

            if (request.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return Json(JsonSerializer.Serialize(new { id = 99 }));
            }

            return null;
        });

    // The scene registrations specifically, not every non-GET: the origin-tag create is a legitimate write on
    // the path that DOES register, so counting writes alone would make the two cases differ by an unrelated
    // call. Both are asserted below — the adds are the damage, the total is the reconciliation.
    private static int Adds(FakeHttpMessageHandler handler)
        => handler.Requests.Count(r =>
            r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));

    private static CoveVideo Owned(int id) => new(
        id, $"Scene {id}", null, [$"10000000-0000-4000-8000-{id:D12}"], [], [$"/data/media/scene-{id}.mp4"], []);

    private static (SceneActions Actions, FakeHttpMessageHandler Handler) Subject(
        HttpStatusCode existence, string entityStashId, int ownedScenes)
    {
        var handler = Instance(existence);
        var client = new WhisparrClient(new HttpClient(handler));
        var library = new FakeCoveLibraryPort();
        library.SeedEntityIdentity(EntityKind.Studio, CoveStudioId, new CoveEntityIdentity([entityStashId], []));
        library.SeedForEntity(
            EntityKind.Studio, CoveStudioId, [.. Enumerable.Range(1, ownedScenes).Select(Owned)]);

        return (new SceneActions(client, Options(), library, new WhisparrCapabilityPort(client)), handler);
    }

    [Fact]
    public async Task An_entity_whisparr_does_not_know_costs_zero_outbound_adds()
    {
        var (actions, handler) = Subject(HttpStatusCode.NotFound, UnknownStudioId, ownedScenes: 5);

        var result = await actions.AddAllMissingAsync(EntityKind.Studio, CoveStudioId, CancellationToken.None);

        var breakdown = WhisparrRequestCounter.Classify(handler);
        Assert.Equal(0, Adds(handler));
        Assert.Equal(0, breakdown.Writes);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.False(result.IsOk);
    }

    [Fact]
    public async Task The_refusal_lands_before_the_add_context_is_resolved()
    {
        var (actions, handler) = Subject(HttpStatusCode.NotFound, UnknownStudioId, ownedScenes: 5);

        await actions.AddAllMissingAsync(EntityKind.Studio, CoveStudioId, CancellationToken.None);

        // The root folder and quality profile are read only when an add is actually going to happen. Their
        // absence is what shows the refusal precedes the batch rather than aborting partway through it.
        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("/api/v3/rootfolder", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("/api/v3/qualityprofile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entity_whisparr_knows_that_holds_nothing_still_registers_its_missing_scenes()
    {
        // The other half of the discrimination: refusing BOTH empty cases would be safe and useless. A known
        // entity holding no scenes is exactly the case this feature exists for.
        var (actions, handler) = Subject(HttpStatusCode.OK, KnownStudioId, ownedScenes: 5);

        var result = await actions.AddAllMissingAsync(EntityKind.Studio, CoveStudioId, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(5, result.Value!.Total);
        Assert.Equal(5, result.Value.Succeeded);
        Assert.Equal(5, Adds(handler));
        // The sixth write is the origin-tag create, which every registering path makes exactly once.
        Assert.Equal(6, WhisparrRequestCounter.Classify(handler).Writes);
    }

    [Fact]
    public async Task An_instance_that_does_not_declare_the_narrow_routes_refuses_rather_than_reading_the_whole_set()
    {
        var handler = FakeHttpMessageHandler.Json("[]").Also(request =>
            request.Url.EndsWith("/docs/v3/openapi.json", StringComparison.Ordinal)
                ? Json("{\"paths\":{\"/api/v3/movie\":{}}}")
                : null);
        var client = new WhisparrClient(new HttpClient(handler));
        var library = new FakeCoveLibraryPort();
        library.SeedEntityIdentity(EntityKind.Studio, CoveStudioId, new CoveEntityIdentity([KnownStudioId], []));
        library.SeedForEntity(EntityKind.Studio, CoveStudioId, [.. Enumerable.Range(1, 5).Select(Owned)]);
        var actions = new SceneActions(client, Options(), library, new WhisparrCapabilityPort(client));

        var result = await actions.AddAllMissingAsync(EntityKind.Studio, CoveStudioId, CancellationToken.None);

        Assert.False(result.IsOk);
        var breakdown = WhisparrRequestCounter.Classify(handler);
        Assert.Equal(0, Adds(handler));
        Assert.Equal(0, breakdown.Writes);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(0, breakdown.NarrowMovieReads);
    }
}
