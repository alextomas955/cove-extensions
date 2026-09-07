using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The outbound cost of a whole-library sync, pinned to a formula: <b>zero whole-movie-set reads for the
/// entire run</b>, and per reflect-owned entity unit <c>1 + ceil(k/1000)</c> movie requests, plus one
/// existence read only when that entity's catalogue is empty.
/// </summary>
/// <remarks>
/// <para>
/// Scoped to v3. A v2 sync reflects owned files through a different, series-shaped walk, so a v3 figure
/// does not describe it and this class does not claim to.
/// </para>
/// <para>
/// The count is proven as a FORMULA rather than as a number: several differently-shaped libraries assert
/// the same relationship, including one whose id-less entities plan no unit at all. One shape would only
/// establish a constant, which the next library size would falsify without anything failing here.
/// </para>
/// <para>
/// The zero is only non-vacuous because the classifier's own ability to tell a narrow read from a whole-set
/// one is exercised directly below. An instrument that called every movie read whole-set would have agreed
/// with the figure this replaced and would keep agreeing now.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class SyncLibraryReadCountTests
{
    private const string BaseUrl = "http://whisparr.local:6969";

    private static WhisparrOptions Options() => new()
    {
        SelectedVersion = "v3",
        BaseUrl = BaseUrl,
        ApiKey = "KEY",
    };

    private static CoveEntityRef Entity(int id, string? stashId)
        => new(id, stashId is null ? [] : [stashId], []);

    // A v3 instance declaring the two narrow catalogue routes, answering each entity's sibling read with
    // `catalogueSize` ids and hydrating exactly the ids it is asked for. `catalogueSize: 0` is the empty case
    // that pays the existence read.
    private static FakeHttpMessageHandler Instance(int catalogueSize)
        => FakeHttpMessageHandler.Json("[]").Also(request =>
        {
            if (request.Url.EndsWith("/docs/v3/openapi.json", StringComparison.Ordinal))
            {
                return Ok(JsonSerializer.Serialize(new
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
                return Ok(JsonSerializer.Serialize(Enumerable.Range(1, catalogueSize)));
            }

            if (request.Url.Contains("/api/v3/studio/", StringComparison.Ordinal)
                || request.Url.Contains("/api/v3/performer/", StringComparison.Ordinal))
            {
                return Ok(JsonSerializer.Serialize(new { id = 7, foreignId = "id", title = "Studio" }));
            }

            if (request.Url.EndsWith("/api/v3/movie/bulk", StringComparison.Ordinal))
            {
                var asked = JsonSerializer.Deserialize<int[]>(request.Body!)!;
                return Ok(JsonSerializer.Serialize(asked.Select(id => new { id, hasFile = false, stashId = $"s-{id}" })));
            }

            return null;
        });

    private static HttpResponseMessage Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body)();

    private static FakeCoveLibraryPort LibraryFor(IEnumerable<CoveEntityRef> studios, IEnumerable<CoveEntityRef> performers)
    {
        var library = new FakeCoveLibraryPort();
        foreach (var entity in studios)
        {
            library.SeedEntityIdentity(EntityKind.Studio, entity.CoveId, new CoveEntityIdentity(entity.StashIds, entity.TpdbIds));
        }

        foreach (var entity in performers)
        {
            library.SeedEntityIdentity(EntityKind.Performer, entity.CoveId, new CoveEntityIdentity(entity.StashIds, entity.TpdbIds));
        }

        return library;
    }

    [Theory]
    // studios and performers with an id, then the id-less ones that must plan no unit and cost nothing
    [InlineData(3, 2, 0, 0)]
    [InlineData(5, 0, 0, 0)]
    [InlineData(2, 4, 0, 0)]
    [InlineData(3, 2, 2, 1)]
    [InlineData(0, 0, 4, 4)]
    public async Task LibrarySync_ReadsTheWholeMovieSet_ZeroTimes(
        int studiosWithId, int performersWithId, int idLessStudios, int idLessPerformers)
    {
        var (reflectUnits, breakdown, _) = await RunSyncAsync(
            studiosWithId, performersWithId, idLessStudios, idLessPerformers, catalogueSize: 7);

        Assert.Equal(studiosWithId + performersWithId, reflectUnits);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(0, breakdown.Writes);
    }

    [Theory]
    // k rows per entity, and the movie-request count one entity costs: 1 sibling + ceil(k/1000) hydrations.
    [InlineData(7, 1)]
    [InlineData(1000, 1)]
    [InlineData(1001, 2)]
    [InlineData(2001, 3)]
    public async Task EachReflectOwnedUnit_CostsOneSiblingReadPlusOneHydrationPerChunk(int k, int chunks)
    {
        var (reflectUnits, breakdown, handler) = await RunSyncAsync(3, 2, 0, 0, catalogueSize: k);

        // Counted by ROUTE rather than off the breakdown's OtherReads bucket, which also carries whatever the
        // unit planner read — an arithmetic remainder would move for a reason that is not this formula.
        Assert.Equal(5, reflectUnits);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(reflectUnits, Count(handler, "/movie/listby"));
        Assert.Equal(reflectUnits * chunks, Count(handler, "/api/v3/movie/bulk"));
        Assert.Equal(reflectUnits * chunks, breakdown.NarrowMovieReads);
        Assert.Equal(0, ExistenceReads(handler));
    }

    [Fact]
    public async Task AnEntityWhoseCatalogueIsEmpty_PaysOneExistenceReadAndHydratesNothing()
    {
        var (reflectUnits, breakdown, handler) = await RunSyncAsync(3, 2, 0, 0, catalogueSize: 0);

        Assert.Equal(5, reflectUnits);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(0, breakdown.NarrowMovieReads);
        Assert.Equal(reflectUnits, Count(handler, "/movie/listby"));
        Assert.Equal(reflectUnits, ExistenceReads(handler));
        Assert.Equal(0, Count(handler, "/api/v3/movie/bulk"));
    }

    [Fact]
    public async Task TheApiDescriptionIsReadOncePerRun_NotOncePerEntity()
    {
        var (reflectUnits, _, handler) = await RunSyncAsync(3, 2, 0, 0, catalogueSize: 7);

        Assert.Equal(5, reflectUnits);
        Assert.Equal(1, Count(handler, "/docs/v3/openapi.json"));
    }

    private static int Count(FakeHttpMessageHandler handler, string fragment)
        => handler.Requests.Count(r => r.Url.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    // The path-form entity reads only: the planner's own `?stashId=` collection queries are a different shape
    // and must not be tallied here.
    private static int ExistenceReads(FakeHttpMessageHandler handler)
        => handler.Requests.Count(r =>
            r.Url.Contains("/api/v3/studio/", StringComparison.Ordinal)
            || r.Url.Contains("/api/v3/performer/", StringComparison.Ordinal));

    private static async Task<(int ReflectUnits, WhisparrRequestBreakdown Breakdown, FakeHttpMessageHandler Handler)> RunSyncAsync(
        int studiosWithId, int performersWithId, int idLessStudios, int idLessPerformers, int catalogueSize)
    {
        var studios = Refs(1000, studiosWithId, idLessStudios);
        var performers = Refs(2000, performersWithId, idLessPerformers);

        var options = Options();
        var handler = Instance(catalogueSize);
        var client = new WhisparrClient(new HttpClient(handler));
        var adapter = AdapterSelector.SelectForVersion(options.SelectedVersion, client)!;

        var units = Ext.BuildSyncUnits(
            studios, performers, [], alsoMonitor: false, MonitorScope.NewReleases, options, adapter);
        var reflectUnits = units.Count(u => u.Op == Ext.SyncOp.ReflectOwned);

        // The runner's reflect-owned arm delegates to exactly this call, and the job constructs ONE
        // SceneActions for the whole fan-out — so a shared instance over a shared handler is the run's own
        // aggregate rather than a per-unit sum, and the capability memo is resolved once for the run.
        var actions = SceneActionsFactory.Build(
            client, options, LibraryFor(studios, performers), SceneActionsFactory.MemoizedPort(client));
        foreach (var unit in units.Where(u => u.Op == Ext.SyncOp.ReflectOwned))
        {
            await actions.ReflectOwnedAsync(unit.Kind, unit.CoveId, default);
        }

        return (reflectUnits, WhisparrRequestCounter.Classify(handler), handler);
    }

    private static CoveEntityRef[] Refs(int idBase, int withId, int without)
        => [
            .. Enumerable.Range(0, withId).Select(i => Entity(idBase + i, $"id-{idBase + i}")),
            .. Enumerable.Range(0, without).Select(i => Entity(idBase + 500 + i, null)),
        ];

    // The counts above, and every later assertion that a narrowing drove a count to zero, are only as
    // good as the classifier's ability to tell the two kinds of read apart. An instrument that called
    // every movie read whole-set would agree with the numbers above AND keep agreeing after a narrowing
    // landed — so its discrimination is exercised directly rather than inferred from a passing total.
    [Theory]
    [InlineData("http://w.local:6969/api/v3/movie", 1, 0, 0)]
    [InlineData("http://w.local:6969/api/v3/movie?excludeLocalCovers=true", 1, 0, 0)]
    [InlineData("http://w.local:6969/api/v3/movie?stashId=", 1, 0, 0)]
    [InlineData("http://w.local:6969/api/v3/movie?stashId=abc", 0, 1, 0)]
    [InlineData("http://w.local:6969/api/v3/movie?tmdbId=7", 0, 1, 0)]
    [InlineData("http://w.local:6969/api/v3/movie?tpdbId=7", 0, 1, 0)]
    [InlineData("http://w.local:6969/api/v3/movie/lookup?term=x", 0, 0, 1)]
    [InlineData("http://w.local:6969/api/v3/movie/listbystudioforeignid?studioForeignId=x", 0, 0, 1)]
    [InlineData("http://w.local:6969/api/v3/qualityprofile", 0, 0, 1)]
    public async Task TheCounter_SeparatesAWholeSetReadFromANarrowOne(
        string url, int wholeSet, int narrow, int otherReads)
    {
        var handler = FakeHttpMessageHandler.Json("[]");
        await new HttpClient(handler).GetAsync(new Uri(url), CancellationToken.None);

        var breakdown = WhisparrRequestCounter.Classify(handler);
        Assert.Equal(wholeSet, breakdown.WholeSetMovieReads);
        Assert.Equal(narrow, breakdown.NarrowMovieReads);
        Assert.Equal(otherReads, breakdown.OtherReads);
        Assert.Equal(0, breakdown.Writes);
    }

    [Fact]
    public async Task TheCounter_TalliesANonGetAsAWrite_NotAsARead()
    {
        var handler = FakeHttpMessageHandler.Json("{}");
        using var content = new StringContent("{}");
        await new HttpClient(handler).PostAsync(
            new Uri("http://w.local:6969/api/v3/movie"), content, default);

        var breakdown = WhisparrRequestCounter.Classify(handler);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(1, breakdown.Writes);
        Assert.Equal(1, breakdown.Total);
    }
}
