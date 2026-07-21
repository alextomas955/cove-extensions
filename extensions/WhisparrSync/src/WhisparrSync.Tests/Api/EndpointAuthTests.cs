using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// Security-critical: the host's <c>[RequiresPermission]</c> filter is inert on minimal-API
/// extension endpoints, so every settings handler enforces the permission itself via
/// <see cref="ICurrentPrincipalAccessor"/>. These prove the deny/allow pair for each route — <c>/status</c>
/// and <c>GET /options</c> gate on <c>extensions.read</c>; the list, webhook-url, save, test-connection and
/// register-webhook routes all gate on <c>extensions.configure</c> (the list + webhook-url routes
/// reach the stored credentials, so a read-only principal must not reach them) — plus that the authorized
/// list path returns the fetched rows, and that the stored API key is never sent to a caller-supplied host.
/// </summary>
public sealed class EndpointAuthTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string StatusJson = "{\"version\":\"3.3.4.808\",\"appName\":\"Whisparr\",\"instanceName\":\"My Whisparr\"}";

    private static Ext NewExtension(FakeStore? store = null)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store ?? new FakeStore());
        return ext;
    }

    private static WhisparrClient ClientReturning(string json)
        => new(new HttpClient(FakeHttpMessageHandler.Json(json)));

    // Exposes the handler so a test can assert the outbound request URL + X-Api-Key header.
    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWithHandler(string json)
    {
        var handler = FakeHttpMessageHandler.Json(json);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    private static async Task<FakeStore> StoreWith(string baseUrl, string apiKey)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options", $"{{\"BaseUrl\":\"{baseUrl}\",\"ApiKey\":\"{apiKey}\",\"SelectedVersion\":\"v3\"}}");
        return store;
    }

    private static string SentApiKey(FakeHttpMessageHandler handler)
        => handler.LastRequest!.Headers.TryGetValues("X-Api-Key", out var values)
            ? string.Concat(values!)
            : string.Empty;

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static Ext.TestConnectionRequest Creds() => new(BaseUrl, "test-key");

    [Fact]
    public async Task TestConnection_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().TestConnectionAsync(
            Creds(), ClientReturning(StatusJson), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task TestConnection_WithConfigure_IsNotForbidden()
    {
        var result = await NewExtension().TestConnectionAsync(
            Creds(), ClientReturning(StatusJson),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.NotEqual(403, StatusOf(result));
    }

    [Fact]
    public async Task Status_WithoutRead_Returns403()
    {
        var result = await NewExtension().StatusAsync(FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task Status_WithRead_IsNotForbidden()
    {
        var result = await NewExtension().StatusAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.NotEqual(403, StatusOf(result));
    }

    [Fact]
    public async Task GetOptions_WithoutRead_Returns403()
    {
        var result = await NewExtension().GetOptionsAsync(FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task GetOptions_WithRead_IsNotForbidden()
    {
        var result = await NewExtension().GetOptionsAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.NotEqual(403, StatusOf(result));
    }

    [Fact]
    public async Task SaveOptions_WithoutConfigure_Returns403()
    {
        var req = new Ext.OptionsSaveRequest(BaseUrl, "k", "v3", 0);
        var result = await NewExtension().SaveOptionsAsync(req, FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task SaveOptions_WithConfigure_IsNotForbidden()
    {
        var req = new Ext.OptionsSaveRequest(BaseUrl, "k", "v3", 0);
        var result = await NewExtension().SaveOptionsAsync(
            req, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.NotEqual(403, StatusOf(result));
    }

    [Fact]
    public async Task SaveOptions_ThenGetOptions_RoundTripsAddDefaults_WithoutEchoingKey()
    {
        // The add-defaults (extra tags, monitor-new default, allow-upgrades) round-trip through
        // the /options wire records, the key is projected out (never echoed), and an empty submitted key preserves the
        // stored one (write-only semantics unchanged). "Search on add" is deliberately absent from the wire.
        const string secretKey = "SECRET-ADDDEF-KEY";
        var ext = NewExtension();
        var configure = FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

        var saved = await ext.SaveOptionsAsync(
            new Ext.OptionsSaveRequest(
                BaseUrl, secretKey, "v3", 3,
                TagsOnAdd: ["cove", "favorites"], MonitorNewByDefault: false, AllowQualityUpgrades: false),
            configure, default);
        var savedView = (OptionsView)Assert.IsAssignableFrom<IValueHttpResult>(saved).Value!;
        Assert.Equal(new[] { "cove", "favorites" }, savedView.TagsOnAdd);
        Assert.False(savedView.MonitorNewByDefault);
        Assert.False(savedView.AllowQualityUpgrades);
        Assert.True(savedView.HasApiKey);

        var loaded = await ext.GetOptionsAsync(FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        var loadedView = (OptionsView)Assert.IsAssignableFrom<IValueHttpResult>(loaded).Value!;
        Assert.Equal(new[] { "cove", "favorites" }, loadedView.TagsOnAdd);
        Assert.False(loadedView.MonitorNewByDefault);
        Assert.False(loadedView.AllowQualityUpgrades);
        Assert.DoesNotContain(secretKey, JsonSerializer.Serialize(loadedView), StringComparison.Ordinal);
        Assert.DoesNotContain(secretKey, JsonSerializer.Serialize(savedView), StringComparison.Ordinal);

        // A follow-up save with a blank key + only one changed toggle preserves the stored key AND the untouched
        // add-defaults (a partial save never resets an unrelated field).
        var resaved = await ext.SaveOptionsAsync(
            new Ext.OptionsSaveRequest(BaseUrl, null, "v3", 3, MonitorNewByDefault: true),
            configure, default);
        var resavedView = (OptionsView)Assert.IsAssignableFrom<IValueHttpResult>(resaved).Value!;
        Assert.True(resavedView.HasApiKey); // stored key preserved on a blank submission
        Assert.True(resavedView.MonitorNewByDefault); // the one changed toggle applied
        Assert.Equal(new[] { "cove", "favorites" }, resavedView.TagsOnAdd); // null tags preserved the prior value
        Assert.False(resavedView.AllowQualityUpgrades); // untouched toggle preserved
    }

    [Fact]
    public async Task RootFolders_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().ListRootFoldersAsync(
            Creds(), ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task RootFolders_WithReadOnly_Returns403()
    {
        // extensions.read is a strictly lower privilege that must no longer reach this route.
        var result = await NewExtension().ListRootFoldersAsync(
            Creds(), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task RootFolders_WithConfigure_ReturnsTheFetchedRows()
    {
        var json = "[{\"id\":7,\"path\":\"/movies\",\"accessible\":true,\"freeSpace\":123}]";
        var result = await NewExtension().ListRootFoldersAsync(
            Creds(), ClientReturning(json),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.NotEqual(403, StatusOf(result));
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        var rows = Assert.IsType<RootFolder[]>(value);
        Assert.Equal(7, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task QualityProfiles_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().ListQualityProfilesAsync(
            Creds(), ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task QualityProfiles_WithReadOnly_Returns403()
    {
        // extensions.read must no longer reach this route.
        var result = await NewExtension().ListQualityProfilesAsync(
            Creds(), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task QualityProfiles_WithConfigure_ReturnsTheFetchedRows()
    {
        var json = "[{\"id\":4,\"name\":\"HD-1080p\"}]";
        var result = await NewExtension().ListQualityProfilesAsync(
            Creds(), ClientReturning(json),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.NotEqual(403, StatusOf(result));
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        var rows = Assert.IsType<QualityProfile[]>(value);
        Assert.Equal(4, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task RootFolders_CallerBaseUrl_WithEmptyKey_NeverSendsTheStoredKey()
    {
        // regression guard: a request that overrides the base URL with a FOREIGN host and submits an
        // empty key must NOT fall back to the stored key — otherwise a caller could exfiltrate the stored
        // Whisparr key to an attacker-controlled server.
        var store = await StoreWith("http://stored.local:6969", "STORED-KEY");
        var (client, handler) = ClientWithHandler("[]");

        var req = new Ext.TestConnectionRequest("http://attacker.example", "");
        var result = await NewExtension(store).ListRootFoldersAsync(
            req, client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.NotEqual(403, StatusOf(result));
        Assert.NotNull(handler.LastRequest);
        Assert.StartsWith("http://attacker.example/", handler.LastRequest!.RequestUri!.ToString());
        // The stored key was withheld: the outbound X-Api-Key is empty, never "STORED-KEY".
        Assert.NotEqual("STORED-KEY", SentApiKey(handler));
        Assert.Equal(string.Empty, SentApiKey(handler));
    }

    [Fact]
    public async Task RootFolders_StoredHost_WithEmptyKey_ReusesTheStoredKey()
    {
        // The legitimate reload path: the UI resends the STORED base URL with an empty key, so the stored
        // key IS reused — but only because the effective host matches the stored host.
        var store = await StoreWith("http://stored.local:6969", "STORED-KEY");
        var (client, handler) = ClientWithHandler("[]");

        var req = new Ext.TestConnectionRequest("http://stored.local:6969", "");
        await NewExtension(store).ListRootFoldersAsync(
            req, client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal("STORED-KEY", SentApiKey(handler));
    }

    [Fact]
    public async Task RootFolders_CallerBaseUrl_WithOwnKey_SendsTheSubmittedKey()
    {
        // The just-tested (unsaved) path: the caller supplies its own key with a new host — that key is used
        // as-is (never the stored one), so the dropdown UX during setup keeps working.
        var store = await StoreWith("http://stored.local:6969", "STORED-KEY");
        var (client, handler) = ClientWithHandler("[]");

        var req = new Ext.TestConnectionRequest("http://new.local:7878", "SUBMITTED-KEY");
        await NewExtension(store).ListRootFoldersAsync(
            req, client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal("SUBMITTED-KEY", SentApiKey(handler));
    }

    private static async Task<FakeStore> StoreWithSavedV2()
    {
        // Active connection is v3; a saved v2 connection (its own host + key) is remembered for the toggle.
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            """{"BaseUrl":"http://stored.local:6969","ApiKey":"STORED-KEY","SelectedVersion":"v3","SavedConnections":{"v2":{"BaseUrl":"http://v2.local:6970","ApiKey":"V2-KEY","RootFolderId":0,"QualityProfileId":0}}}""");
        return store;
    }

    [Fact]
    public async Task RootFolders_SavedConnectionHost_WithEmptyKey_ReusesThatConnectionsKey()
    {
        // Toggling versions loads the OTHER version's saved URL with a blank key. A saved connection's key is
        // bound to its OWN host, so pairing them is not exfiltration — this is what repopulates the other
        // instance's root/profile dropdowns without re-typing its key.
        var store = await StoreWithSavedV2();
        var (client, handler) = ClientWithHandler("[]");

        var req = new Ext.TestConnectionRequest("http://v2.local:6970", "");
        await NewExtension(store).ListRootFoldersAsync(
            req, client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        // The SAVED v2 key is sent — NOT the active STORED-KEY (no cross-version bleed) and NOT empty.
        Assert.Equal("V2-KEY", SentApiKey(handler));
    }

    [Fact]
    public async Task RootFolders_UnknownHost_WithEmptyKey_WithheldEvenWhenSavedConnectionsExist()
    {
        // A host matching NEITHER the active NOR any saved connection still withholds every stored key.
        var store = await StoreWithSavedV2();
        var (client, handler) = ClientWithHandler("[]");

        var req = new Ext.TestConnectionRequest("http://attacker.example", "");
        await NewExtension(store).ListRootFoldersAsync(
            req, client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal(string.Empty, SentApiKey(handler));
        Assert.NotEqual("V2-KEY", SentApiKey(handler));
    }

    [Fact]
    public async Task TestConnection_StoredHost_WithEmptyKey_ReusesTheStoredKey()
    {
        // Regression: once a key is saved the settings field is masked ("Key is set — type to replace"),
        // so re-testing a stored connection sends the STORED base URL with an EMPTY key. Test connection
        // must fall back to the stored key (it previously tested with an empty key → false "rejected").
        var store = await StoreWith("http://stored.local:6969", "STORED-KEY");
        var (client, handler) = ClientWithHandler(StatusJson);

        var req = new Ext.TestConnectionRequest("http://stored.local:6969", "");
        var result = await NewExtension(store).TestConnectionAsync(
            req, client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal("STORED-KEY", SentApiKey(handler));
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        Assert.Equal("success", value!.GetType().GetProperty("result")!.GetValue(value));
    }

    [Fact]
    public async Task TestConnection_ForeignHost_WithEmptyKey_NeverSendsTheStoredKey()
    {
        // guard for Test connection: a foreign host + empty key must NOT fall back to the stored
        // key (no exfiltration of the stored Whisparr key to a caller-chosen server).
        var store = await StoreWith("http://stored.local:6969", "STORED-KEY");
        var (client, handler) = ClientWithHandler(StatusJson);

        var req = new Ext.TestConnectionRequest("http://attacker.example", "");
        await NewExtension(store).TestConnectionAsync(
            req, client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.NotEqual("STORED-KEY", SentApiKey(handler));
        Assert.Equal(string.Empty, SentApiKey(handler));
    }

    [Fact]
    public async Task WebhookUrl_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().WebhookUrlAsync("http://cove.local", FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task WebhookUrl_WithReadOnly_Returns403()
    {
        // minting/persisting the webhook secret is a configure action; read-only must not reach it.
        var result = await NewExtension().WebhookUrlAsync(
            "http://cove.local", FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task WebhookUrl_WithConfigure_IsNotForbidden()
    {
        var result = await NewExtension().WebhookUrlAsync(
            "http://cove.local", FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.NotEqual(403, StatusOf(result));
    }

    [Fact]
    public async Task RegisterWebhook_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().RegisterWebhookAsync(
            "http://cove.local", ClientReturning("{}"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task RegisterWebhook_WithConfigure_IsNotForbidden()
    {
        // Register runs after a successful test/save, so a base URL is stored — seed one so the outbound
        // POST targets an absolute URI (the deny path never reaches the client).
        var store = new FakeStore();
        await store.SetAsync("options", "{\"BaseUrl\":\"http://localhost:6969\",\"ApiKey\":\"k\",\"SelectedVersion\":\"v3\"}");
        var result = await NewExtension(store).RegisterWebhookAsync(
            "http://cove.local", ClientReturning("{\"id\":1}"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.NotEqual(403, StatusOf(result));
    }
}
