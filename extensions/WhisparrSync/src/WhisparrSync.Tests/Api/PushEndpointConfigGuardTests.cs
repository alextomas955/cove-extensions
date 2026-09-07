using System.Net;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The push slice's stored-configuration refusal: <c>/scene-add</c>, <c>/scene-monitor</c> and
/// <c>/bulk-add-missing</c> refuse an incomplete stored configuration BEFORE any outbound call, plus the add
/// path's per-add quality-profile derivation. Complements <see cref="MonitorEndpointTests"/>, which pins the
/// same guard on the studio/performer monitor route.
/// </summary>
/// <remarks>
/// The fixture seeds a real SQLite <c>CoveContext</c> and registers it as the scope's <see cref="DbContext"/>,
/// because the per-scene guard sits AFTER the identity refusal and the identity comes from a Cove database read:
/// without a resolvable scene every per-scene case would stop at <c>NO_STASHDB_IDENTITY</c> and prove nothing
/// about the guard. That host double is what makes this the host-double tier rather than the endpoint one.
/// </remarks>
[Trait("Tier", "L1")]
public sealed class PushEndpointConfigGuardTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";
    private const string StashDbEndpoint = "https://stashdb.org/graphql"; // the WhisparrOptions default
    private const string TpdbEndpoint = "https://theporndb.net/graphql";
    private const string StashUuid = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";

    private static async Task<FakeStore> StoreWith(string baseUrl = StoredBaseUrl, string apiKey = StoredKey)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            $"{{\"BaseUrl\":\"{baseUrl}\",\"ApiKey\":\"{apiKey}\",\"SelectedVersion\":\"v3\"}}");
        return store;
    }

    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWith(params Func<HttpResponseMessage>[] responses)
    {
        var handler = FakeHttpMessageHandler.Sequence(responses);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static string ProfileList(params int[] ids)
        => JsonSerializer.Serialize(ids.Select(id => new { id, name = $"Profile {id}" }));

    // Filtered on the path, never on position: the fake handler consumes its steps positionally, so any
    // position- or total-count-based tally breaks the moment a step is inserted ahead of it.
    private static int ProfileReads(FakeHttpMessageHandler handler)
        => handler.Requests.Count(r => r.Url.Contains("/qualityprofile", StringComparison.OrdinalIgnoreCase));

    // The profile list, the root read and the tag read are all GETs, so a non-GET is a write to Whisparr.
    private static int MutatingCalls(FakeHttpMessageHandler handler)
        => handler.Requests.Count(r => r.Method != HttpMethod.Get);

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string ResponseJson(IResult result)
        => JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    // An extension whose Cove-side scene resolution succeeds. The host resolves the library through a scoped
    // DbContext, so the fixture registers a real one and lets InitializeAsync capture the scope factory the
    // handler reads it through — the same host path FolderOverlapEndpointTests uses for Cove roots, and no
    // test-only seam into the handler.
    private static async Task<(Ext Ext, int CoveId, IAsyncDisposable Db, IAsyncDisposable Conn)> NewExtensionWithSceneAsync(
        FakeStore store, string endpoint = StashDbEndpoint)
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        db.Set<Video>().Add(new Video
        {
            Title = "Scene A",
            RemoteIds = { new VideoRemoteId { Endpoint = endpoint, RemoteId = StashUuid } },
        });
        await db.SaveChangesAsync();
        var coveId = db.Set<Video>().AsNoTracking().Single().Id;

        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store);
        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        await ext.InitializeAsync(services.BuildServiceProvider());
        return (ext, coveId, db, conn);
    }

    // ---- an unset address is fatal on EVERY leg of the three guarded routes, flip included ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SceneMonitor_WithBlankStoredAddress_Refuses400_OnBothLegs(bool monitored)
    {
        var (ext, coveId, db, conn) = await NewExtensionWithSceneAsync(await StoreWith(baseUrl: ""));
        try
        {
            var (client, handler) = ClientWith();

            var result = await ext.SceneMonitorAsync(
                new SceneMonitorRequest(coveId, monitored), client, default);

            Assert.Equal(400, StatusOf(result));
            var json = ResponseJson(result);
            Assert.Contains("CONFIG_INCOMPLETE", json, StringComparison.Ordinal);
            Assert.Contains("baseUrl", json, StringComparison.Ordinal);
            Assert.Equal(0, handler.CallCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SceneAdd_WithBlankStoredApiKey_Refuses400_WithTheKeyName()
    {
        var (ext, coveId, db, conn) = await NewExtensionWithSceneAsync(await StoreWith(apiKey: ""));
        try
        {
            var (client, handler) = ClientWith();

            var result = await ext.SceneAddAsync(new SceneAddRequest(coveId), client, default);

            Assert.Equal(400, StatusOf(result));
            var json = ResponseJson(result);
            Assert.Contains("CONFIG_INCOMPLETE", json, StringComparison.Ordinal);
            Assert.Contains("apiKey", json, StringComparison.Ordinal);
            Assert.Equal(0, handler.CallCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task BulkAddMissing_WithBlankStoredAddress_Refuses400_WithTheAddressKey()
    {
        var (ext, _, db, conn) = await NewExtensionWithSceneAsync(await StoreWith(baseUrl: ""));
        try
        {
            var (client, handler) = ClientWith();

            var result = await ext.BulkAddMissingAsync(
                new BulkAddMissingRequest("studio", 7), client, default);

            Assert.Equal(400, StatusOf(result));
            var json = ResponseJson(result);
            Assert.Contains("CONFIG_INCOMPLETE", json, StringComparison.Ordinal);
            Assert.Contains("baseUrl", json, StringComparison.Ordinal);
            Assert.Equal(0, handler.CallCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // ---- the boundary: the routes that consume no quality profile resolve none ----

    [Fact]
    public async Task SceneMonitor_Off_ReachesWhisparr_AndResolvesNoProfile()
    {
        // A flip echoes the existing Whisparr record's own quality profile; the OFF leg resolves none.
        var (ext, coveId, db, conn) = await NewExtensionWithSceneAsync(await StoreWith());
        try
        {
            var (client, handler) = ClientWith(Ok("[]")); // the OFF leg's movie lookup

            var result = await ext.SceneMonitorAsync(
                new SceneMonitorRequest(coveId, Monitored: false), client, default);

            Assert.DoesNotContain("CONFIG_INCOMPLETE", ResponseJson(result), StringComparison.Ordinal);
            Assert.True(handler.CallCount > 0, "the OFF leg must proceed past the guard and reach Whisparr");
            Assert.Equal(0, ProfileReads(handler));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SceneSearch_ReachesWhisparr_AndResolvesNoProfile()
    {
        // A search grabs an ALREADY-added movie, which supplies its own profile.
        var (ext, coveId, db, conn) = await NewExtensionWithSceneAsync(await StoreWith());
        try
        {
            var (client, handler) = ClientWith(Ok("[]")); // the movie-index read

            var result = await ext.SceneSearchAsync(new SceneSearchRequest(coveId), client, default);

            Assert.DoesNotContain("CONFIG_INCOMPLETE", ResponseJson(result), StringComparison.Ordinal);
            Assert.True(handler.CallCount > 0, "the search route must reach Whisparr");
            Assert.Equal(0, ProfileReads(handler));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SceneExclusion_ReachesWhisparr_AndResolvesNoProfile()
    {
        // An exclusion writes an import-list entry, which carries no profile at all.
        var (ext, coveId, db, conn) = await NewExtensionWithSceneAsync(await StoreWith());
        try
        {
            var (client, handler) = ClientWith(Ok("[]"));

            var result = await ext.SceneExclusionAsync(
                new SceneExclusionRequest(coveId, Exclude: true), client, default);

            Assert.DoesNotContain("CONFIG_INCOMPLETE", ResponseJson(result), StringComparison.Ordinal);
            Assert.True(handler.CallCount > 0, "the exclusion route must reach Whisparr");
            Assert.Equal(0, ProfileReads(handler));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SceneAdd_WithCompleteConfiguration_IsNotRefused_AndReachesWhisparr()
    {
        var (ext, coveId, db, conn) = await NewExtensionWithSceneAsync(await StoreWith());
        try
        {
            var (client, handler) = ClientWith(Ok(RootFolderList), Ok(TagListWithOrigin), Ok(ProfileList(4)), Ok("[]"));

            var result = await ext.SceneAddAsync(new SceneAddRequest(coveId), client, default);

            Assert.DoesNotContain("CONFIG_INCOMPLETE", ResponseJson(result), StringComparison.Ordinal);
            Assert.True(handler.CallCount > 0, "a complete configuration must reach the orchestration seam");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // ---- the add's quality profile comes from the instance, not from a stored value ----

    [Fact]
    public async Task SceneAdd_CarriesTheFirstProfileTheInstanceOffers()
    {
        var (ext, coveId, db, conn) = await NewExtensionWithSceneAsync(await StoreWith());
        try
        {
            var (client, handler) = ClientWith(
                Ok(RootFolderList), Ok(TagListWithOrigin), Ok(ProfileList(7, 1, 2)), Ok("[]"));

            await ext.SceneAddAsync(new SceneAddRequest(coveId), client, default);

            var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
            Assert.Contains("\"qualityProfileId\":7", post.Body, StringComparison.Ordinal);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SceneAdd_WhenTheInstanceOffersNoProfile_ReachesNoMutatingPath()
    {
        // Whisparr's create validator would reject the add; the resolve fails first, sending nothing.
        var (ext, coveId, db, conn) = await NewExtensionWithSceneAsync(await StoreWith());
        try
        {
            var (client, handler) = ClientWith(Ok(RootFolderList), Ok(TagListWithOrigin), Ok("[]"));

            var result = await ext.SceneAddAsync(new SceneAddRequest(coveId), client, default);

            Assert.Equal(502, StatusOf(result));
            Assert.Equal(0, MutatingCalls(handler));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // ---- refusal precedence: permission, then identity, then configuration ----

    [Fact]
    public async Task SceneAdd_WithNoConnectedIdentity_GetsTheIdentityOutcome_NotTheConfigurationOne()
    {
        // The client's control shows the identity reason ahead of the configuration one, so the server must
        // answer the same way: a scene the connected generation cannot identify is NOT told to fix a setting.
        var (ext, coveId, db, conn) = await NewExtensionWithSceneAsync(
            await StoreWith(), endpoint: TpdbEndpoint);
        try
        {
            var (client, handler) = ClientWith();

            var result = await ext.SceneAddAsync(new SceneAddRequest(coveId), client, default);

            var json = ResponseJson(result);
            Assert.Contains("NO_STASHDB_IDENTITY", json, StringComparison.Ordinal);
            Assert.DoesNotContain("CONFIG_INCOMPLETE", json, StringComparison.Ordinal);
            Assert.Equal(0, handler.CallCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private static string RootFolderList => JsonSerializer.Serialize(new[]
    {
        new { id = 2, path = "/data/media", accessible = true, freeSpace = 1L },
    });

    private static string TagListWithOrigin => JsonSerializer.Serialize(new[]
    {
        new { id = 5, label = "cove-sync" },
    });
}
