using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests;

/// <summary>
/// Security-critical (T-03-03): the host's <c>[RequiresPermission]</c> filter is inert on minimal-API
/// extension endpoints, so every settings handler enforces the permission itself via
/// <see cref="ICurrentPrincipalAccessor"/>. These prove the deny/allow pair for each route — <c>/status</c>
/// and <c>GET /options</c> gate on <c>extensions.read</c>; the list, webhook-url, save, test-connection and
/// register-webhook routes all gate on <c>extensions.configure</c> (CR-01: the list + webhook-url routes
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

    // Exposes the handler so a test can assert the outbound request URL + X-Api-Key header (CR-01).
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
        var req = new Ext.OptionsSaveRequest(BaseUrl, "k", "v3", 0, 0);
        var result = await NewExtension().SaveOptionsAsync(req, FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task SaveOptions_WithConfigure_IsNotForbidden()
    {
        var req = new Ext.OptionsSaveRequest(BaseUrl, "k", "v3", 0, 0);
        var result = await NewExtension().SaveOptionsAsync(
            req, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.NotEqual(403, StatusOf(result));
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
        // CR-01: extensions.read is a strictly lower privilege that must no longer reach this route.
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
        // CR-01: extensions.read must no longer reach this route.
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
        // CR-01 regression guard: a request that overrides the base URL with a FOREIGN host and submits an
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

    [Fact]
    public async Task WebhookUrl_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().WebhookUrlAsync("http://cove.local", FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task WebhookUrl_WithReadOnly_Returns403()
    {
        // CR-01: minting/persisting the webhook secret is a configure action; read-only must not reach it.
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
