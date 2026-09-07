using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Push;
using WhisparrSync.SceneStatus;
using WhisparrSync.Tests.TestSupport;
using static WhisparrSync.Tests.TestSupport.EndpointTestSupport;

namespace WhisparrSync.Tests.Discovery;

/// <summary>
/// Security-critical: the host's <c>[RequiresPermission]</c> filter is inert on minimal-API extension endpoints,
/// so the discovery MUTATION route (<c>/discovery/action</c>) enforces <c>extensions.configure</c> itself. These
/// prove, for the per-card Monitor op: the 403-first deny trio (null / read-only / no-configure → 403); that v2
/// defers <c>VERSION_UNSUPPORTED</c> BEFORE any wire call (no per-scene add on v2); that a source id not in the
/// re-derived missing set is rejected <c>NOT_IN_MISSING_SET</c> with no add (the loop-safety gate: an action can
/// only ever target a non-owned catalogue scene); and that an unknown op is a clean <c>400 UNKNOWN_OP</c>.
/// </summary>
[Trait("Tier", "L2")]
public sealed partial class DiscoveryActionEndpointTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";

    private static DiscoveryActionRequest Monitor(string sourceId = "catalogue-scene-x")
        => new(CoveEntityId: 5, Kind: "studio", SourceId: sourceId, Op: "monitor");

    // ---- v2 defers VERSION_UNSUPPORTED BEFORE any wire call (no per-scene add on v2) ----

    [Fact]
    public async Task Monitor_V2Instance_DefersVersionUnsupported_WithNoWireCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey, version: "v2");
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAsync(
            Monitor(), client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount); // deferred BEFORE any Whisparr call
    }

    // ---- a source id not in the re-derived missing set is rejected with no add (loop-safety gate) ----

    [Fact]
    public async Task Monitor_SourceIdNotInMissingSet_RejectsNotInMissingSet()
    {
        // With no host DB scope the server resolves no remote id for the entity, so the re-derived missing set is
        // empty — any client-held source id is out-of-set and rejected, never an add of an arbitrary id.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAsync(
            Monitor("not-in-the-set"), client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("NOT_IN_MISSING_SET", ResponseJson(result), StringComparison.Ordinal);
        // No add ever reached Whisparr: no POST /movie was issued for the out-of-set id.
        Assert.DoesNotContain(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
    }

    // ---- an incomplete stored configuration is refused BEFORE the missing-set re-derive ----

    [Fact]
    public void ConfigurationGuard_PrecedesTheMissingSetRederive_InTheSharedPreamble()
    {
        // A structural guard, for the same reason the page-forwarding one is: with no host DB scope the re-derive
        // short-circuits before issuing a request, so a zero request count is ALSO zero for a guard placed after it
        // — the behavioural assertion cannot tell the two apart. The source can. Comments are stripped first, so a
        // doc comment can neither satisfy nor invalidate the check.
        var code = SourceCodeText(ActionEndpointsSourcePath());
        var guard = code.IndexOf("RefuseIncompleteConfig(", StringComparison.Ordinal);
        var derive = code.IndexOf("ComputeDiscoveryAsync(", StringComparison.Ordinal);

        Assert.True(guard >= 0 && derive >= 0);
        Assert.True(guard < derive, "the configuration refusal must precede the preamble's missing-set re-derive");
    }

    [Theory]
    [InlineData("monitor")]
    [InlineData("unmonitor")]
    public async Task DiscoveryAction_WithBlankStoredAddress_Refuses400_OnBothPreambleLegs(string op)
    {
        // An unset address is fatal on both legs of the shared preamble: with no host there is no request to make.
        var store = await StoreWith("", StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAsync(
            new DiscoveryActionRequest(5, "studio", "catalogue-scene-x", op),
            client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        var json = ResponseJson(result);
        Assert.Contains("CONFIG_INCOMPLETE", json, StringComparison.Ordinal);
        Assert.Contains("baseUrl", json, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- an unknown op is a clean 400 (the switch is extensible for a later release) ----

    [Fact]
    public async Task UnknownOp_Returns400()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var result = await NewExtension(store).DiscoveryActionAsync(
            new DiscoveryActionRequest(5, "studio", "catalogue-scene-x", "delete"),
            ClientReturning("[]"), StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("UNKNOWN_OP", ResponseJson(result), StringComparison.Ordinal);
    }

    // ---- the stored key is never echoed to the response ----

    [Fact]
    public async Task Monitor_ResponseNeverContainsTheApiKey()
    {
        const string secretKey = "SUPER-SECRET-KEY-62b1";
        var store = await StoreWith(StoredBaseUrl, secretKey);
        var result = await NewExtension(store).DiscoveryActionAsync(
            Monitor(), ClientReturning("[]"), StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.DoesNotContain(secretKey, ResponseJson(result), StringComparison.Ordinal);
    }

    // ======================= /discovery/action-all (bulk Monitor) =======================
    // The bulk endpoint mirrors the single one's ordering: 403-first, op parse, well-formed body, a supplied
    // selection capped BEFORE any work, then v2 deferred BEFORE any enqueue. The mark-wanted BODY (every targeted
    // scene ends monitored:true + searchForMovie:false, no grab, idempotent) + the SourceIds intersect are proven
    // on the extracted host-free core, mirroring how the videos-batch core is unit-tested independent of the host.

    private static DiscoveryActionAllRequest MonitorAll(string[]? sourceIds = null)
        => new(CoveEntityId: 5, Kind: "studio", Op: "monitor", SourceIds: sourceIds);

    [Fact]
    public async Task ActionAll_UnknownOp_Returns400()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var result = await NewExtension(store).DiscoveryActionAllAsync(
            new DiscoveryActionAllRequest(5, "studio", "delete", null),
            ClientReturning("[]"), StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("UNKNOWN_OP", ResponseJson(result), StringComparison.Ordinal);
    }

    // ---- a supplied selection is capped BEFORE any per-item work (fan-out containment) ----

    [Fact]
    public async Task ActionAll_TooManySourceIds_Returns400_BeforeAnyWireCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");
        var oversized = Enumerable.Range(0, 1001).Select(i => $"scene-{i}").ToArray();

        var result = await NewExtension(store).DiscoveryActionAllAsync(
            MonitorAll(oversized), client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("TOO_MANY_IDS", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount); // capped before any missing-set re-derive / Whisparr call
    }

    // ---- v2 defers VERSION_UNSUPPORTED BEFORE any enqueue / wire call (no per-scene add on v2) ----

    [Fact]
    public async Task ActionAll_V2Instance_DefersVersionUnsupported_BeforeEnqueue()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey, version: "v2");
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAllAsync(
            MonitorAll(["catalogue-scene-x"]), client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- the bulk route refuses an incomplete configuration BEFORE enqueue, per op ----

    [Theory]
    [InlineData("monitor")]
    [InlineData("unmonitor")]
    [InlineData("search")]
    public async Task ActionAll_WithBlankStoredAddress_Refuses400_OnEveryOp(string op)
    {
        var store = await StoreWith("", StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAllAsync(
            new DiscoveryActionAllRequest(5, "studio", op, ["catalogue-scene-x"]),
            client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        var json = ResponseJson(result);
        Assert.Contains("CONFIG_INCOMPLETE", json, StringComparison.Ordinal);
        Assert.Contains("baseUrl", json, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- the bulk CORE: intersect + cap-independent targeting (host-free, no HTTP) ----

    private static MissingScene Scene(string sourceId, string title = "A Scene", string? date = "2021-03-03")
        => new(sourceId, title, date, "A Studio", PosterUrl: null);

    [Fact]
    public void TargetedSceneRefs_WholeMissingSet_WhenSourceIdsOmitted()
    {
        var missing = new[] { Scene("a"), Scene("b"), Scene("c") };
        var refs = global::WhisparrSync.WhisparrSync.TargetedSceneRefs(missing, null);
        Assert.Equal(["a", "b", "c"], refs.Select(r => r.StashId));
    }

    [Fact]
    public void TargetedSceneRefs_IntersectsSuppliedSelectionWithMissingSet_OutOfSetSkipped_OrderPreserved()
    {
        var missing = new[] { Scene("a"), Scene("b"), Scene("c") };
        // A selection naming one in-set id + one that is NOT in the missing set: only the in-set id targets, and
        // the missing-set order is preserved (never an add of an arbitrary out-of-set id).
        var refs = global::WhisparrSync.WhisparrSync.TargetedSceneRefs(missing, ["c", "zzz-not-in-set", "a"]);
        Assert.Equal(["a", "c"], refs.Select(r => r.StashId));
    }

    [Fact]
    public void TargetedSceneRefs_IsCaseInsensitiveOnTheSourceId()
    {
        var missing = new[] { Scene("Catalogue-Scene-X") };
        var refs = global::WhisparrSync.WhisparrSync.TargetedSceneRefs(missing, ["catalogue-scene-x"]);
        Assert.Single(refs);
        Assert.Equal("Catalogue-Scene-X", refs[0].StashId);
    }

    // ---- the bulk CORE end-to-end: every targeted scene ends monitored:true with NO grab, idempotent ----

    private const string BulkStashId = "9c8b7a6d-1e2f-4a3b-8c9d-0e1f2a3b4c5d";
    private static readonly int[] BulkOriginTags = [5];

    private static WhisparrOptions BulkV3Options => new()
    {
        BaseUrl = StoredBaseUrl,
        ApiKey = StoredKey,
        SelectedVersion = "v3",
        DetectedVersion = "3.3.4.808",
    };

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static string RootFolderList => JsonSerializer.Serialize(new[]
    {
        new { id = 2, path = "/data/media", accessible = true, freeSpace = 1L },
    });

    private static string TagList => JsonSerializer.Serialize(new[]
    {
        new { id = 5, label = AddContextResolver.OriginTagLabel },
    });

    private static string ProfileList => JsonSerializer.Serialize(new[]
    {
        new { id = 4, name = "HD-1080p" },
    });

    private static string BulkMovie(bool monitored) => JsonSerializer.Serialize(new
    {
        id = 42,
        foreignId = BulkStashId,
        stashId = BulkStashId,
        title = "A Scene",
        monitored,
        hasFile = false,
        qualityProfileId = 4,
        rootFolderPath = "/data/media",
        tags = BulkOriginTags,
    });

    private static SceneActions BulkActions(FakeHttpMessageHandler handler)
        => SceneActionsFactory.Build(new WhisparrClient(new HttpClient(handler)), BulkV3Options, new FakeCoveLibraryPort());

    private static void AssertNeverGrabs(FakeHttpMessageHandler handler)
    {
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            handler.Requests,
            r => r.Body?.Replace(" ", "", StringComparison.Ordinal)
                .Contains("\"searchForMovie\":true", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task ActionAllCore_MarksEveryTargetedSceneMonitoredTrue_WithNoGrab()
    {
        // The whole re-derived missing set (SourceIds omitted): a present-unmonitored missing scene is flipped
        // monitored:true (a PUT) — it joins the wanted list — and NO grab command fires.
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),                     // root resolve (monitor-ON add leg prereq)
            Ok(TagList),                            // origin-tag ensure (found)
            Ok(ProfileList),                        // quality-profile resolve
            Ok($"[{BulkMovie(monitored: false)}]"), // GET movie -> present, unmonitored
            Ok(BulkMovie(monitored: true)));        // PUT flip -> monitored:true

        var result = await global::WhisparrSync.WhisparrSync.DiscoveryActionAllCoreAsync(
            [Scene(BulkStashId)], sourceIds: null, BulkActions(handler), default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Succeeded);

        var put = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/api/v3/movie/", StringComparison.Ordinal));
        Assert.Contains("\"monitored\":true", put.Body);
        // No add POST (the movie already exists), and never a grab.
        Assert.DoesNotContain(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        AssertNeverGrabs(handler);
    }

    [Fact]
    public async Task ActionAllCore_OutOfSetSourceId_IssuesNoAdd()
    {
        // A supplied selection whose only id is NOT in the missing set marks nothing — the intersect drops it, so
        // the core reaches Whisparr for no per-scene work (an action can only ever target a non-owned scene).
        var (client, handler) = ClientWithHandler("[]");
        var actions = SceneActionsFactory.Build(client, BulkV3Options, new FakeCoveLibraryPort());

        var result = await global::WhisparrSync.WhisparrSync.DiscoveryActionAllCoreAsync(
            [Scene(BulkStashId)], sourceIds: ["not-in-the-set"], actions, default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(0, result.Value!.Total);
        Assert.Equal(0, handler.CallCount); // no add, no wire call for an out-of-set id
    }

    [Fact]
    public async Task ActionAllCore_ReRun_IsIdempotent_WithNoGrab()
    {
        // Re-running over an already-monitored scene re-asserts monitored:true (never a duplicate add) with no grab.
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagList),
            Ok(ProfileList),                       // quality-profile resolve
            Ok($"[{BulkMovie(monitored: true)}]"), // GET movie -> present, already monitored
            Ok(BulkMovie(monitored: true)));       // PUT (idempotent)

        var result = await global::WhisparrSync.WhisparrSync.DiscoveryActionAllCoreAsync(
            [Scene(BulkStashId)], sourceIds: null, BulkActions(handler), default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Succeeded);
        Assert.DoesNotContain(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        AssertNeverGrabs(handler);
    }

    // ======================= /discovery/action Op=unmonitor + Op=search =======================
    // The single-op endpoints mirror the Monitor ordering. Unmonitor is a v3-only PUT flip (monitored:false, no
    // command); Search is the SOLE immediate grab, version-uniform, resolving the movie id server-side from the
    // raw index (a not-added scene is searched:false with no command).

    private static DiscoveryActionRequest Unmonitor(string sourceId = "catalogue-scene-x")
        => new(CoveEntityId: 5, Kind: "studio", SourceId: sourceId, Op: "unmonitor");

    private static DiscoveryActionRequest SearchNow(string sourceId = "catalogue-scene-x")
        => new(CoveEntityId: 5, Kind: "studio", SourceId: sourceId, Op: "search");

    // ---- Unmonitor defers v2 BEFORE any wire call (per-scene monitor is the v3-only push capability) ----

    [Fact]
    public async Task Unmonitor_V2Instance_DefersVersionUnsupported_WithNoWireCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey, version: "v2");
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAsync(
            Unmonitor(), client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount); // deferred BEFORE any Whisparr call
    }

    // ---- Unmonitor of a source id not in the re-derived missing set is rejected with no wire mutation ----

    [Fact]
    public async Task Unmonitor_SourceIdNotInMissingSet_RejectsNotInMissingSet()
    {
        // With no host DB scope the server resolves no remote id, so the missing set is empty — any client-held
        // source id is out-of-set and rejected, never an unmonitor of an arbitrary id.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAsync(
            Unmonitor("not-in-the-set"), client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("NOT_IN_MISSING_SET", ResponseJson(result), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    // ---- Search over an unresolvable source id is a handled searched:false with NO command (loop-safe) ----

    [Fact]
    public async Task Search_UnresolvableSourceId_ReturnsSearchedFalse_NoCommand()
    {
        // No host DB scope → an empty missing set + empty movie index → the source id resolves to no added movie,
        // so Search is a handled searched:false and issues no grab command.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAsync(
            SearchNow(), client, StashDbClientReturning(), TpdbClientReturning(), default);

        // A serialized { searched: false } (never an error result) — nothing to grab, and no command was issued.
        Assert.Contains("\"searched\":false", ResponseJson(result), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Search is version-uniform: on v2 it is not deferred; an unresolvable id is still searched:false ----

    [Fact]
    public async Task Search_V2Instance_IsNotDeferred_ReturnsSearchedFalse_NoCommand()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey, version: "v2");
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAsync(
            SearchNow(), client, StashDbClientReturning(), TpdbClientReturning(), default);

        // A serialized { searched: false } — NOT a VERSION_UNSUPPORTED defer — proves search is version-uniform.
        var json = ResponseJson(result);
        Assert.Contains("\"searched\":false", json, StringComparison.Ordinal);
        Assert.DoesNotContain("VERSION_UNSUPPORTED", json, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the page coordinate: clamped, fail-closed, and byte-identical to today when omitted ----
    // A per-card action may name the page its row was rendered from, which bounds the re-derive to that one
    // provider page. The coordinate is untrusted: it is clamped to a valid 1-based index, and a page that does not
    // contain the acted-on source id reaches the SAME refusal an out-of-set id already reaches, with no mutation.
    // Both generations may carry a page (search is version-uniform and is not gated server-side), which is
    // admissible only because that refusal is fail-closed.

    private static DiscoveryActionRequest SearchOnPage(int? page, string sourceId = "catalogue-scene-x")
        => new(CoveEntityId: 5, Kind: "studio", SourceId: sourceId, Op: "search", Page: page);

    [Fact]
    public async Task Search_PageNotContainingSourceId_FailsClosed_WithNoMutation()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAsync(
            SearchOnPage(7, "not-on-that-page"), client, StashDbClientReturning(), TpdbClientReturning(), default);

        // Search's fail-closed form is the handled searched:false with no command; the 400 NOT_IN_MISSING_SET is
        // the monitor/unmonitor prelude's form (Monitor_PageNotContainingSourceId_FailsClosed_WithNoMutation).
        Assert.Contains("\"searched\":false", ResponseJson(result), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Monitor_PageNotContainingSourceId_FailsClosed_WithNoMutation()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAsync(
            new DiscoveryActionRequest(5, "studio", "not-on-that-page", "monitor", Page: 7),
            client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("NOT_IN_MISSING_SET", ResponseJson(result), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.DoesNotContain(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public async Task Search_NonPositivePage_IsClamped_NeverErrorsOrLoops(int page)
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAsync(
            SearchOnPage(page), client, StashDbClientReturning(), TpdbClientReturning(), default);

        // A clean 200 with the honest outcome — never a 500 and never an unbounded walk.
        Assert.Contains("\"searched\":false", ResponseJson(result), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_PageOmitted_PreservesWholeCatalogueDerive()
    {
        // An omitted coordinate reproduces the shipped whole-catalogue outcome exactly — the same response body
        // Search_UnresolvableSourceId_ReturnsSearchedFalse_NoCommand pins for a page-free request.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

        var pageAbsent = await NewExtension(store).DiscoveryActionAsync(
            SearchNow(), client, StashDbClientReturning(), TpdbClientReturning(), default);
        var pageExplicitlyNull = await NewExtension(store).DiscoveryActionAsync(
            SearchOnPage(null), client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(ResponseJson(pageAbsent), ResponseJson(pageExplicitlyNull));
        Assert.Contains("\"searched\":false", ResponseJson(pageAbsent), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the SEARCH CORE end-to-end (host-free): only an in-set, added scene issues exactly one MoviesSearch ----

    private static Dictionary<string, WhisparrMovie> IndexOf(params WhisparrMovie[] movies)
    {
        var index = new Dictionary<string, WhisparrMovie>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in movies)
        {
            if (!string.IsNullOrEmpty(m.StashId))
            {
                index[m.StashId] = m;
            }
        }

        return index;
    }

    private static WhisparrMovie AddedMovie(int id, string stashId) => new(
        Id: id, Title: "A Scene", Year: 2021, StashId: stashId, ForeignId: stashId, ItemType: "scene",
        Monitored: true, HasFile: false, MovieFile: null);

    [Fact]
    public async Task SearchCore_InSetAddedScene_IssuesExactlyOneMoviesSearch()
    {
        var handler = FakeHttpMessageHandler.Json("{}"); // POST /command (MoviesSearch) -> queued
        var result = await global::WhisparrSync.WhisparrSync.DiscoverySearchCoreAsync(
            [Scene(BulkStashId)], IndexOf(AddedMovie(42, BulkStashId)), BulkStashId, BulkActions(handler), default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value); // a grab was issued

        var command = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"MoviesSearch\"", command.Body);
        Assert.Contains("\"movieIds\":[42]", command.Body);
    }

    [Fact]
    public async Task SearchCore_ProductionMovieIndex_ResolvesAddedMovie_IssuesExactlyOneMoviesSearch()
    {
        // The index built by the PRODUCTION selector, not by the local IndexOf: a direct-catalogue row is
        // synthesized with Id 0 while the reconciliation set holds the same stash id with the real movie id.
        // Sourcing the v3 action index from the catalogue makes every per-card search a no-op, which is the
        // regression this pins.
        var synthesized = new WhisparrMovie(
            Id: 0, Title: "A Scene", Year: 2021, StashId: BulkStashId, ForeignId: BulkStashId, ItemType: "scene",
            Monitored: false, HasFile: false, MovieFile: null);
        var reconciliationIndex = SceneStatusProjector.BuildMovieIndex([AddedMovie(42, BulkStashId)]);

        var actionIndex = global::WhisparrSync.WhisparrSync.ActionMovieIndex(
            isV2: false, [synthesized], reconciliationIndex);
        Assert.Equal(42, actionIndex[BulkStashId].Id);

        var handler = FakeHttpMessageHandler.Json("{}"); // POST /command (MoviesSearch) -> queued
        var result = await global::WhisparrSync.WhisparrSync.DiscoverySearchCoreAsync(
            [Scene(BulkStashId)], actionIndex, BulkStashId, BulkActions(handler), default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);

        var command = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"MoviesSearch\"", command.Body);
        Assert.Contains("\"movieIds\":[42]", command.Body);

        // v2 keeps the TPDB-keyed catalogue index (a v2 episode id exists nowhere else), and a v3 movie-set read
        // that did not return Ok leaves the action index empty — an unresolvable id, and no command.
        var v2Index = global::WhisparrSync.WhisparrSync.ActionMovieIndex(isV2: true, [synthesized], reconciliationIndex);
        Assert.Equal(0, v2Index[BulkStashId].Id);
        Assert.Empty(global::WhisparrSync.WhisparrSync.ActionMovieIndex(
            isV2: false, [synthesized], new Dictionary<string, WhisparrMovie>(0)));
    }

    [Fact]
    public async Task SearchCore_InSetNotAddedScene_ReturnsSearchedFalse_NoCommand()
    {
        // A scene in the missing set but with no added movie (a synthesized Id 0 row) is searched:false — nothing
        // to grab, no command.
        var (client, handler) = ClientWithHandler("{}");
        var actions = SceneActionsFactory.Build(client, BulkV3Options, new FakeCoveLibraryPort());

        var notAdded = new WhisparrMovie(
            Id: 0, Title: "A Scene", Year: 2021, StashId: BulkStashId, ForeignId: BulkStashId, ItemType: "scene",
            Monitored: false, HasFile: false, MovieFile: null);
        var result = await global::WhisparrSync.WhisparrSync.DiscoverySearchCoreAsync(
            [Scene(BulkStashId)], IndexOf(notAdded), BulkStashId, actions, default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value);
        Assert.Equal(0, handler.CallCount); // no command for a not-added scene
    }

    [Fact]
    public async Task SearchCore_OutOfSetSourceId_ReturnsSearchedFalse_NoCommand()
    {
        // A source id not in the missing set never grabs, even if it happens to resolve in the index (loop-safety:
        // Search can only ever target a non-owned catalogue scene).
        var (client, handler) = ClientWithHandler("{}");
        var actions = SceneActionsFactory.Build(client, BulkV3Options, new FakeCoveLibraryPort());

        var result = await global::WhisparrSync.WhisparrSync.DiscoverySearchCoreAsync(
            [Scene("a-different-scene")], IndexOf(AddedMovie(42, BulkStashId)), BulkStashId, actions, default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- the UNMONITOR CORE end-to-end (host-free): a PUT monitored:false with NO command; absent = safe no-op ----

    [Fact]
    public async Task UnmonitorCore_PresentMonitoredScene_FlipsMonitoredFalse_NoCommandNoAdd()
    {
        // A wanted (present + monitored) scene is flipped monitored:false via a PUT — no add, no grab.
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{BulkMovie(monitored: true)}]"), // GET movie -> present, monitored
            Ok(BulkMovie(monitored: false)));      // PUT flip -> monitored:false

        var result = await global::WhisparrSync.WhisparrSync.DiscoveryActionAllUnitCoreAsync(
            global::WhisparrSync.WhisparrSync.DiscoverySceneOp.Unmonitor,
            [Scene(BulkStashId)], sourceIds: null, IndexOf(), BulkActions(handler), default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Succeeded);

        var put = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/api/v3/movie/", StringComparison.Ordinal));
        Assert.Contains("\"monitored\":false", put.Body);
        Assert.DoesNotContain(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        AssertNeverGrabs(handler);
    }

    [Fact]
    public async Task UnmonitorCore_NotInWhisparrScene_IsSafeNoOp_NoAddNoPutNoCommand()
    {
        // A missing scene not present in Whisparr (the direct-catalogue case): the un-path finds no movie
        // (MovieId 0) and does nothing — never an add, never a PUT, never a grab.
        var (client, handler) = ClientWithHandler("[]"); // GET movie -> absent
        var actions = SceneActionsFactory.Build(client, BulkV3Options, new FakeCoveLibraryPort());

        var result = await global::WhisparrSync.WhisparrSync.DiscoveryActionAllUnitCoreAsync(
            global::WhisparrSync.WhisparrSync.DiscoverySceneOp.Unmonitor,
            [Scene(BulkStashId)], sourceIds: null, IndexOf(), actions, default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Succeeded); // the safe no-op is a completed unit, not a failure
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
        AssertNeverGrabs(handler);
    }

    [Fact]
    public async Task ActionAll_WholeTag_IsRefused_SoOneClickCannotMarkTensOfThousandsWanted()
    {
        // A null SourceIds means "the whole re-derived missing set". A tag's catalogue spans the entire library, so
        // the unbounded form is refused server-side — the UI omitting the button is not an authorization check.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAllAsync(
            new DiscoveryActionAllRequest(1, "tag", "monitor", null),
            client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("WHOLE_TAG_NOT_ALLOWED", ResponseJson(result), StringComparison.Ordinal);
        // Nothing reached Whisparr — the refusal precedes every mutation.
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ActionAll_BoundedTagSelection_IsNotRefused()
    {
        // Only the unbounded whole-entity form is withheld; a bounded selection stays available on a tag.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, _) = ClientWithHandler("[]");

        var result = await NewExtension(store).DiscoveryActionAllAsync(
            new DiscoveryActionAllRequest(1, "tag", "monitor", ["src-1", "src-2"]),
            client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.DoesNotContain("WHOLE_TAG_NOT_ALLOWED", ResponseJson(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActionAll_SelectionWithPage_DerivesOnePage_AndFailsClosedOutOfPage()
    {
        // A bulk selection may name the page its rows were rendered from. A selected id the derived page does not
        // hold is dropped by the same intersect an out-of-set id already meets — no command, no add, no flip — and
        // every guard above the derive keeps firing first: the whole-tag refusal and the selection cap both answer
        // before a single wire call, page or no page.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

        var (client, handler) = ClientWithHandler("[]");
        var result = await NewExtension(store).DiscoveryActionAllAsync(
            new DiscoveryActionAllRequest(5, "studio", "search", ["not-on-that-page"], Page: 4),
            client, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Contains("\"total\":0", ResponseJson(result), StringComparison.Ordinal);

        var (tagClient, tagHandler) = ClientWithHandler("[]");
        var wholeTag = await NewExtension(store).DiscoveryActionAllAsync(
            new DiscoveryActionAllRequest(1, "tag", "monitor", null, Page: 4),
            tagClient, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(wholeTag));
        Assert.Contains("WHOLE_TAG_NOT_ALLOWED", ResponseJson(wholeTag), StringComparison.Ordinal);
        Assert.Equal(0, tagHandler.CallCount);

        var (capClient, capHandler) = ClientWithHandler("[]");
        var oversized = await NewExtension(store).DiscoveryActionAllAsync(
            new DiscoveryActionAllRequest(
                5, "studio", "monitor", [.. Enumerable.Range(0, 1001).Select(i => $"scene-{i}")], Page: 4),
            capClient, StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(400, StatusOf(oversized));
        Assert.Contains("TOO_MANY_IDS", ResponseJson(oversized), StringComparison.Ordinal);
        Assert.Equal(0, capHandler.CallCount);
    }

    [Fact]
    public void ActionSource_ForwardsTheClampedPageAtEveryDeriveCallSite()
    {
        // A structural guard, because the L2 harness has no host DB scope: with no scope the derive short-circuits
        // at the no-source-id state, which makes every page yield the same empty missing set and leaves the forward
        // itself behaviourally unobservable. The source IS observable, and a derive an action can reach without
        // handing it the page is the unbounded whole-catalogue walk — measured at 390 catalogue rows behind one
        // click where the paged read of the same entity read 40. Mirrors the source-guard idiom in
        // NoMutationTests.CoordinatorSource_ContainsNoFilesystemMoveOrDeleteApi.
        var code = SourceCodeText(ActionEndpointsSourcePath());
        var callSites = code.Split("ComputeDiscoveryAsync(", StringSplitOptions.None).Skip(1).ToArray();

        // The shared prelude (monitor + unmonitor), the search handler, the inline bulk path and the background job.
        // BOTH halves of the coordinate are asserted: a page index alone names no set once an ordering exists, and
        // a site that carried only one half would re-derive a different page than the row was rendered from.
        Assert.Equal(4, callSites.Length);
        Assert.All(
            callSites,
            tail => Assert.StartsWith(
                "kind, coveEntityId, client, stashDbClient, tpdbClient, ct, page, query)",
                tail.TrimStart(),
                StringComparison.Ordinal));
    }

    // The production source with comments removed and whitespace collapsed: a doc comment naming the derive can
    // neither satisfy nor invalidate the guard, and a call wrapped across lines reads the same as a one-liner.
    private static string SourceCodeText(string path)
    {
        var code = string.Join(
            ' ',
            File.ReadAllLines(path)
                .Select(line => line.IndexOf("//", StringComparison.Ordinal) is var i and >= 0 ? line[..i] : line)
                .Where(line => !string.IsNullOrWhiteSpace(line)));
        return SourceWhitespace().Replace(code, " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SourceWhitespace();

    // The action-endpoint source sits beside this test project: ../../WhisparrSync/Discovery/DiscoveryActionEndpoints.cs
    // (this test file lives one concern-subfolder deep under the test project root).
    private static string ActionEndpointsSourcePath([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "WhisparrSync", "Discovery", "DiscoveryActionEndpoints.cs"));

    [Fact]
    public async Task ActionAll_WholeStudioAndPerformer_StayAllowed()
    {
        // The capability is withheld from tags only — a studio's and a performer's catalogue are bounded by the
        // entity's own output, so their whole-entity form is unchanged.
        var store = await StoreWith(StoredBaseUrl, StoredKey);

        foreach (var kind in new[] { "studio", "performer" })
        {
            var (client, _) = ClientWithHandler("[]");
            var result = await NewExtension(store).DiscoveryActionAllAsync(
                new DiscoveryActionAllRequest(1, kind, "monitor", null),
                client, StashDbClientReturning(), TpdbClientReturning(), default);

            Assert.DoesNotContain("WHOLE_TAG_NOT_ALLOWED", ResponseJson(result), StringComparison.Ordinal);
        }
    }

    // ---- a client-supplied filter id at the READ endpoint ----

    private static DiscoveryEntityRequest Read(DiscoveryQueryRequest? query = null)
        => new(CoveEntityId: 5, Kind: "studio", Page: 1, Query: query);

    // A read that answered: the handler returns a defaulted Results.Json, which carries no explicit status code,
    // while every refusal on this route sets one.
    private const int Answered = 0;

    [Fact]
    public async Task Entity_WithAForeignOrMalformedFilterId_IsAnsweredRatherThanRefused()
    {
        // A filter id is the ONE untrusted value on this read, and it arrives from a URL a reader can edit. A
        // foreign but well-formed id, a string of the wrong shape, one carrying url-significant characters and an
        // impossible year must each leave a normal answer for the entity the SERVER resolved. A hand-edited url
        // never turns the tab into an error page: the guard DROPS what it cannot trust and the read succeeds.
        //
        // That the filtered set is a SUBSET of the entity's own is NOT provable here: this harness has no host DB
        // scope, so no remote id resolves, no provider is reached, and both sides of any such comparison would be
        // empty. It is proven where it is observable — the built provider request — over 3 kinds x 16 filter
        // combinations in StashDbDiscoverySourceTests, falsified there by making the entity criterion
        // conditional, and by the ThePornDB url assertions in TpdbDiscoverySourceTests.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var unfiltered = await NewExtension(store).DiscoveryEntityAsync(
            Read(), ClientReturning("[]"), StashDbClientReturning(), TpdbClientReturning(), default);
        var baseline = ResponseJson(unfiltered);

        DiscoveryQueryRequest[] hostile =
        [
            new(StudioId: "99999999-9999-4999-8999-999999999999"),
            new(PerformerId: "88888888-8888-4888-8888-888888888888"),
            new(TagId: "' OR 1=1 --"),
            new(StudioId: "29&site_id=4520"),
            new(Year: 99999),
        ];

        foreach (var query in hostile)
        {
            var result = await NewExtension(store).DiscoveryEntityAsync(
                Read(query), ClientReturning("[]"), StashDbClientReturning(), TpdbClientReturning(), default);

            // A defaulted Results.Json carries NO explicit status (the host serves it 200); every refusal on
            // this route sets one. Zero here is therefore "answered", and a 400 would fail this line.
            Assert.Equal(Answered, StatusOf(result));
            // Byte-identical to the unfiltered read of the SAME entity: a caller's filter id cannot move which
            // entity is read, only narrow within it, and here there is nothing of that entity to narrow.
            Assert.Equal(baseline, ResponseJson(result));
        }
    }

    [Fact]
    public async Task Entity_NonServedRead_ClaimsNoFacetCapabilityAtAll()
    {
        // A read that never reached a provider knows nothing about what a provider could do. Emitting the
        // capability arrays here would let the tab suppress its page-derived label over options no provider
        // supplied — the over-claim in its purest form, on the one path where nothing at all was measured.
        var store = await StoreWith(StoredBaseUrl, StoredKey);

        var result = await NewExtension(store).DiscoveryEntityAsync(
            Read(), ClientReturning("[]"), StashDbClientReturning(), TpdbClientReturning(), default);

        Assert.Equal(Answered, StatusOf(result));

        // Asserted on the TYPED value, not on a serialized string. ResponseJson renders the result object with a
        // DEFAULT serializer, whose casing differs from the wire's — a name check there passes whatever casing
        // the members happen to have and proves nothing about their absence.
        var body = Assert.IsType<DiscoveryResult>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(DiscoveryState.NoSourceId, body.State);
        Assert.Null(body.ServerSideSorts);
        Assert.Null(body.ServerSideFacets);
        Assert.Null(body.WholeSetFacetAxes);
        Assert.Null(body.FacetOptions);
    }
}
