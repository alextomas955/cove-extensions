using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Plugins;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Scene;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Reconcile;

/// <summary>
/// The reconciliation-status enrichment: a <c>/preview-sync</c> row carries a
/// <c>whisparrState</c> derived from its Whisparr movie + the exclusion read, with EXCLUSION-FIRST
/// precedence (an excluded movie is <c>Excluded</c> even when it has a file). Drives the extracted
/// <see cref="Ext.ComputeReconciliationCoreAsync"/> seam with a fake-HTTP <see cref="WhisparrClient"/>
/// (movie read then exclusion read) + a fake <see cref="FakeCoveLibraryPort"/> — no scope, no live DB —
/// and asserts the flattened rows. Also pins the wire casing: <see cref="SceneWhisparrState"/>
/// serializes as its camelCase name even under a PascalCase-policy options bag.
/// </summary>
public sealed class ReconStatusTests
{
    private const string BaseUrl = "http://stored.local:6969";
    private const string ApiKey = "STORED-KEY";

    private static Ext NewExtension()
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(new FakeStore());
        return ext;
    }

    // A fake adapter over an ordered two-step transport: call 1 answers the movie read, call 2 the exclusion
    // read — the exact order ComputeReconciliationCoreAsync issues them.
    private static V3Adapter AdapterFor(string moviesJson, string exclusionsJson)
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", moviesJson),
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", exclusionsJson));
        return new V3Adapter(new WhisparrClient(new HttpClient(handler)));
    }

    private static string Movies() => JsonSerializer.Serialize(new[]
    {
        new { id = 10, title = "Excluded Scene", year = 2020, stashId = "uuid-excluded", foreignId = "uuid-excluded", itemType = "scene", monitored = true, hasFile = true },
        new { id = 20, title = "Monitored With File Scene", year = 2021, stashId = "uuid-dl", foreignId = "uuid-dl", itemType = "scene", monitored = true, hasFile = true },
        new { id = 30, title = "Monitored Scene", year = 2022, stashId = "uuid-mon", foreignId = "uuid-mon", itemType = "scene", monitored = true, hasFile = false },
    });

    private static string Exclusions() => JsonSerializer.Serialize(new[]
    {
        new { id = 1, foreignId = "uuid-excluded", title = "Excluded Scene", year = 2020 },
    });

    private static async Task<Ext.ReconResponse> RunAsync(string moviesJson, string exclusionsJson)
    {
        var ext = NewExtension();
        var (error, diff, excluded) = await ext.ComputeReconciliationCoreAsync(
            AdapterFor(moviesJson, exclusionsJson), new FakeCoveLibraryPort(), BaseUrl, ApiKey, CancellationToken.None);

        Assert.Null(error);
        return Ext.ToReconResponse(diff!, excluded!);
    }

    private static SceneWhisparrState StateOf(Ext.ReconResponse response, int movieId)
        => response.Rows.Single(r => r.WhisparrMovieId == movieId).WhisparrState;

    // Mirrors the real ReconciliationResponseJsonOptions: Web (camelCase property names) + a naming-policy-FREE
    // JsonStringEnumConverter. The type-level converter on SceneWhisparrState still forces camelCase VALUES —
    // this options bag would otherwise emit PascalCase, so it proves the pin wins. Cached (CA1869).
    private static readonly JsonSerializerOptions PascalPolicyEnumOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task ExcludedMovie_Row_IsExcluded_EvenWithFile()
    {
        // Exclusion-first: movie 10 is monitored with a file (would be Monitored) but its StashId is in the exclusion read.
        var response = await RunAsync(Movies(), Exclusions());
        Assert.Equal(SceneWhisparrState.Excluded, StateOf(response, 10));
    }

    [Fact]
    public async Task NonExcludedMonitoredMovieWithFile_Row_IsMonitored()
    {
        // Monitored-primary: a file no longer overrides the state — a monitored+downloaded row stays Monitored.
        var response = await RunAsync(Movies(), Exclusions());
        Assert.Equal(SceneWhisparrState.Monitored, StateOf(response, 20));
    }

    [Fact]
    public async Task MonitoredMovieWithoutFile_Row_IsMonitored()
    {
        var response = await RunAsync(Movies(), Exclusions());
        Assert.Equal(SceneWhisparrState.Monitored, StateOf(response, 30));
    }

    [Fact]
    public async Task EmptyExclusionRead_LeavesTheMonitoredFileMovieMonitored()
    {
        // A v2 instance defers the exclusion read (empty set here stands in for that degrade): no "excluded"
        // rows, the movie is classified purely on its own facts (monitored → Monitored, file is secondary).
        var response = await RunAsync(Movies(), "[]");
        Assert.Equal(SceneWhisparrState.Monitored, StateOf(response, 10));
    }

    // ---- the SceneWhisparrState wire string is pinned to camelCase ----

    [Fact]
    public async Task ReconRow_WhisparrState_SerializesAsCamelCase()
    {
        var response = await RunAsync(Movies(), Exclusions());
        var json = JsonSerializer.Serialize(response, PascalPolicyEnumOptions);

        Assert.Contains("\"whisparrState\":\"excluded\"", json, StringComparison.Ordinal);
        Assert.Contains("\"whisparrState\":\"monitored\"", json, StringComparison.Ordinal);
        // NOT the PascalCase spelling a plain enum converter would emit.
        Assert.DoesNotContain("\"whisparrState\":\"Excluded\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"whisparrState\":\"Monitored\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SceneDetail_State_SerializesAsCamelCase_IncludingNotAdded()
    {
        // The scene-detail projection uses the same pinned enum; confirm the NotAdded compound name camelCases.
        var index = SceneStatusProjector.BuildMovieIndex([]);
        var excluded = SceneStatusProjector.BuildExcludedSet([]);
        var detail = SceneStatusProjector.Detail(["no-match"], index, excluded);
        var json = JsonSerializer.Serialize(detail, PascalPolicyEnumOptions);

        Assert.Equal(SceneWhisparrState.NotAdded, detail.State);
        Assert.Contains("\"state\":\"notAdded\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("NotAdded", json, StringComparison.Ordinal);
    }
}
