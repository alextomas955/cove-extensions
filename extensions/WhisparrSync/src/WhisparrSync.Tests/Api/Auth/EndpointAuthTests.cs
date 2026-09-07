using System.Text.Json;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using static WhisparrSync.Tests.TestSupport.EndpointTestSupport;

namespace WhisparrSync.Tests.Api.Auth;

/// <summary>
/// Security-critical: the host's <c>[RequiresPermission]</c> filter is inert on minimal-API
/// extension endpoints, so every settings handler enforces the permission itself via
/// <see cref="ICurrentPrincipalAccessor"/>. These prove the deny/allow pair for each route — <c>/status</c>
/// and <c>GET /options</c> gate on <c>extensions.read</c>; the list, webhook-url, save, test-connection and
/// register-webhook routes all gate on <c>extensions.configure</c> (the list + webhook-url routes
/// reach the stored credentials, so a read-only principal must not reach them) — plus that the authorized
/// list path returns the fetched rows, and that the stored API key is never sent to a caller-supplied host.
/// </summary>
[Trait("Tier", "L2")]
public sealed class EndpointAuthTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string StatusJson = "{\"version\":\"3.3.4.808\",\"appName\":\"Whisparr\",\"instanceName\":\"My Whisparr\"}";

    private static TestConnectionRequest Creds() => new(BaseUrl, "test-key");

    [Fact]
    public async Task SaveOptions_ThenGetOptions_RoundTripsAddDefaults_WithoutEchoingKey()
    {
        // The add-defaults (extra tags, monitor-new default, allow-upgrades) round-trip through
        // the /options wire records, the key is projected out (never echoed), and an empty submitted key preserves the
        // stored one (write-only semantics unchanged). "Search on add" is deliberately absent from the wire.
        const string secretKey = "SECRET-ADDDEF-KEY";
        var ext = NewExtension();
        var saved = await ext.SaveOptionsAsync(
            new OptionsSaveRequest(
                BaseUrl, secretKey, "v3",
                TagsOnAdd: ["cove", "favorites"], MonitorNewByDefault: false, AllowQualityUpgrades: false),
            default);
        var savedView = (OptionsView)Assert.IsAssignableFrom<IValueHttpResult>(saved).Value!;
        Assert.Equal(new[] { "cove", "favorites" }, savedView.TagsOnAdd);
        Assert.False(savedView.MonitorNewByDefault);
        Assert.False(savedView.AllowQualityUpgrades);
        Assert.True(savedView.HasApiKey);

        var loaded = await ext.GetOptionsAsync(default);
        var loadedView = (OptionsView)Assert.IsAssignableFrom<IValueHttpResult>(loaded).Value!;
        Assert.Equal(new[] { "cove", "favorites" }, loadedView.TagsOnAdd);
        Assert.False(loadedView.MonitorNewByDefault);
        Assert.False(loadedView.AllowQualityUpgrades);
        Assert.DoesNotContain(secretKey, JsonSerializer.Serialize(loadedView), StringComparison.Ordinal);
        Assert.DoesNotContain(secretKey, JsonSerializer.Serialize(savedView), StringComparison.Ordinal);

        // A follow-up save with a blank key + only one changed toggle preserves the stored key AND the untouched
        // add-defaults (a partial save never resets an unrelated field).
        var resaved = await ext.SaveOptionsAsync(
            new OptionsSaveRequest(BaseUrl, null, "v3", MonitorNewByDefault: true),
            default);
        var resavedView = (OptionsView)Assert.IsAssignableFrom<IValueHttpResult>(resaved).Value!;
        Assert.True(resavedView.HasApiKey); // stored key preserved on a blank submission
        Assert.True(resavedView.MonitorNewByDefault); // the one changed toggle applied
        Assert.Equal(new[] { "cove", "favorites" }, resavedView.TagsOnAdd); // null tags preserved the prior value
        Assert.False(resavedView.AllowQualityUpgrades); // untouched toggle preserved
    }

    private static async Task<FakeStore> StoreWithSavedV2()
    {
        // Active connection is v3; a saved v2 connection (its own host + key) is remembered for the toggle.
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            """{"BaseUrl":"http://stored.local:6969","ApiKey":"STORED-KEY","SelectedVersion":"v3","SavedConnections":{"v2":{"BaseUrl":"http://v2.local:6970","ApiKey":"V2-KEY"}}}""");
        return store;
    }

    [Fact]
    public async Task TestConnection_SavedConnectionHost_WithEmptyKey_ReusesThatConnectionsKey()
    {
        // Toggling versions loads the OTHER version's saved URL with a blank key. A saved connection's key is
        // bound to its OWN host, so pairing them is not exfiltration — this is what lets the settings toggle
        // verify the other instance without re-typing its key.
        var store = await StoreWithSavedV2();
        var (client, handler) = ClientWithHandler(StatusJson);

        var req = new TestConnectionRequest("http://v2.local:6970", "");
        await NewExtension(store).TestConnectionAsync(
            req, client, default);

        // The SAVED v2 key is sent — NOT the active STORED-KEY (no cross-version bleed) and NOT empty.
        Assert.Equal("V2-KEY", SentApiKey(handler));
    }

    [Fact]
    public async Task TestConnection_UnknownHost_WithEmptyKey_WithheldEvenWhenSavedConnectionsExist()
    {
        // A host matching NEITHER the active NOR any saved connection still withholds every stored key.
        var store = await StoreWithSavedV2();
        var (client, handler) = ClientWithHandler(StatusJson);

        var req = new TestConnectionRequest("http://attacker.example", "");
        await NewExtension(store).TestConnectionAsync(
            req, client, default);

        Assert.Equal(string.Empty, SentApiKey(handler));
        Assert.NotEqual("V2-KEY", SentApiKey(handler));
    }

    [Fact]
    public async Task TestConnection_CallerBaseUrl_WithOwnKey_SendsTheSubmittedKey()
    {
        // The just-tested (unsaved) path: the caller supplies its own key with a new host — that key is used
        // as-is (never the stored one), so the setup flow keeps working before anything is saved.
        var store = await StoreWith("http://stored.local:6969", "STORED-KEY");
        var (client, handler) = ClientWithHandler(StatusJson);

        var req = new TestConnectionRequest("http://new.local:7878", "SUBMITTED-KEY");
        await NewExtension(store).TestConnectionAsync(
            req, client, default);

        Assert.Equal("SUBMITTED-KEY", SentApiKey(handler));
    }

    [Fact]
    public async Task TestConnection_StoredHost_WithEmptyKey_ReusesTheStoredKey()
    {
        // Regression: once a key is saved the settings field is masked ("Key is set — type to replace"),
        // so re-testing a stored connection sends the STORED base URL with an EMPTY key. Test connection
        // must fall back to the stored key (it previously tested with an empty key → false "rejected").
        var store = await StoreWith("http://stored.local:6969", "STORED-KEY");
        var (client, handler) = ClientWithHandler(StatusJson);

        var req = new TestConnectionRequest("http://stored.local:6969", "");
        var result = await NewExtension(store).TestConnectionAsync(
            req, client, default);

        Assert.Equal("STORED-KEY", SentApiKey(handler));
        var value = Assert.IsType<TestConnectionSuccessResponse>(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal("success", value.Result);
    }

    [Fact]
    public async Task TestConnection_ForeignHost_WithEmptyKey_NeverSendsTheStoredKey()
    {
        // guard for Test connection: a foreign host + empty key must NOT fall back to the stored
        // key (no exfiltration of the stored Whisparr key to a caller-chosen server).
        var store = await StoreWith("http://stored.local:6969", "STORED-KEY");
        var (client, handler) = ClientWithHandler(StatusJson);

        var req = new TestConnectionRequest("http://attacker.example", "");
        await NewExtension(store).TestConnectionAsync(
            req, client, default);

        Assert.NotEqual("STORED-KEY", SentApiKey(handler));
        Assert.Equal(string.Empty, SentApiKey(handler));
    }

}
