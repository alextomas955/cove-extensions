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
/// <see cref="ICurrentPrincipalAccessor"/>. These prove the deny/allow pair for each route — reads gate on
/// <c>extensions.read</c>, writes on <c>extensions.configure</c> — and that the authorized list path
/// returns the fetched rows, and the webhook-url / register-webhook routes gate the same way.
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
    public async Task RootFolders_WithoutRead_Returns403()
    {
        var result = await NewExtension().ListRootFoldersAsync(
            Creds(), ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task RootFolders_WithRead_ReturnsTheFetchedRows()
    {
        var json = "[{\"id\":7,\"path\":\"/movies\",\"accessible\":true,\"freeSpace\":123}]";
        var result = await NewExtension().ListRootFoldersAsync(
            Creds(), ClientReturning(json),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);

        Assert.NotEqual(403, StatusOf(result));
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        var rows = Assert.IsType<RootFolder[]>(value);
        Assert.Equal(7, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task QualityProfiles_WithoutRead_Returns403()
    {
        var result = await NewExtension().ListQualityProfilesAsync(
            Creds(), ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task QualityProfiles_WithRead_ReturnsTheFetchedRows()
    {
        var json = "[{\"id\":4,\"name\":\"HD-1080p\"}]";
        var result = await NewExtension().ListQualityProfilesAsync(
            Creds(), ClientReturning(json),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);

        Assert.NotEqual(403, StatusOf(result));
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        var rows = Assert.IsType<QualityProfile[]>(value);
        Assert.Equal(4, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task WebhookUrl_WithoutRead_Returns403()
    {
        var result = await NewExtension().WebhookUrlAsync("http://cove.local", FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task WebhookUrl_WithRead_IsNotForbidden()
    {
        var result = await NewExtension().WebhookUrlAsync(
            "http://cove.local", FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
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
