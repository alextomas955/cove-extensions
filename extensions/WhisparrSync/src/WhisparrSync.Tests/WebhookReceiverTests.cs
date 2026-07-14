using System.Text;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Ingest;
using WhisparrSync.Matching;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// SEC-01 / IMPT-01 for the anonymous, token-gated <c>/webhook</c> receiver: the shared-secret token is
/// validated FIRST (constant-time) before anything is parsed, a valid <c>Test</c> ping is a 200 no-op, a
/// valid <c>Download</c> ingests the imported path via the in-process <see cref="IScanService"/>, an
/// unknown event type is a 200 ignore, and the token is accepted from either the header or the
/// <c>?token=</c> query. No Cove principal is present on any of these requests (the inverted auth model).
/// </summary>
public sealed class WebhookReceiverTests
{
    private const string Secret = "s3cr3t-webhook-token";
    private const string VideoPath = "/data/media/Scene (2024)/Scene.mkv";

    private static async Task<FakeStore> StoreWithSecret(string secret = Secret)
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(new WhisparrOptions { WebhookSecret = secret });
        return store;
    }

    private static (WebhookReceiver Receiver, FakeScanService Scan) NewReceiver(FakeStore store)
    {
        var scan = new FakeScanService();
        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => scan);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var coordinator = new IngestCoordinator(scopeFactory);
        return (new WebhookReceiver(store, coordinator), scan);
    }

    private static DefaultHttpContext Context(string body, string? headerToken = null, string? queryToken = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (headerToken is not null)
        {
            http.Request.Headers["X-Cove-Token"] = headerToken;
        }

        if (queryToken is not null)
        {
            http.Request.QueryString = new QueryString($"?token={queryToken}");
        }

        return http;
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    [Fact]
    public async Task ValidToken_Download_IngestsTheImportedPathAsVideo()
    {
        var (receiver, scan) = NewReceiver(await StoreWithSecret());

        var result = await receiver.HandleAsync(Context(WebhookPayloads.Download(VideoPath), headerToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        var call = Assert.Single(scan.Imports);
        Assert.Equal("Video", call.Kind);
        Assert.Equal(VideoPath, call.Path);
    }

    [Fact]
    public async Task ValidToken_Test_Is200NoOp()
    {
        var (receiver, scan) = NewReceiver(await StoreWithSecret());

        var result = await receiver.HandleAsync(Context(WebhookPayloads.Test, headerToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        Assert.Empty(scan.Imports);
    }

    [Fact]
    public async Task MissingToken_Is401_AndDoesNotIngest()
    {
        var (receiver, scan) = NewReceiver(await StoreWithSecret());

        var result = await receiver.HandleAsync(Context(WebhookPayloads.Download(VideoPath)), default);

        Assert.Equal(401, StatusOf(result));
        Assert.Empty(scan.Imports);
    }

    [Fact]
    public async Task WrongToken_Is401_AndDoesNotIngest()
    {
        var (receiver, scan) = NewReceiver(await StoreWithSecret());

        var result = await receiver.HandleAsync(
            Context(WebhookPayloads.Download(VideoPath), headerToken: "not-the-secret"), default);

        Assert.Equal(401, StatusOf(result));
        Assert.Empty(scan.Imports);
    }

    [Fact]
    public async Task WrongToken_EvenOnTestEvent_Is401()
    {
        var (receiver, scan) = NewReceiver(await StoreWithSecret());

        var result = await receiver.HandleAsync(Context(WebhookPayloads.Test, headerToken: "wrong"), default);

        Assert.Equal(401, StatusOf(result));
        Assert.Empty(scan.Imports);
    }

    [Fact]
    public async Task EmptyStoredSecret_FailsClosed_401()
    {
        // A never-configured secret must reject every event fail-closed, not accept an empty presented token.
        var (receiver, scan) = NewReceiver(await StoreWithSecret(secret: string.Empty));

        var result = await receiver.HandleAsync(
            Context(WebhookPayloads.Download(VideoPath), headerToken: string.Empty), default);

        Assert.Equal(401, StatusOf(result));
        Assert.Empty(scan.Imports);
    }

    [Fact]
    public async Task ValidToken_UnknownEventType_Is200Ignore()
    {
        var (receiver, scan) = NewReceiver(await StoreWithSecret());

        var result = await receiver.HandleAsync(
            Context(WebhookPayloads.WithEventType("Rename"), headerToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        Assert.Empty(scan.Imports);
    }

    [Fact]
    public async Task QueryToken_Fallback_IsAccepted()
    {
        var (receiver, scan) = NewReceiver(await StoreWithSecret());

        var result = await receiver.HandleAsync(Context(WebhookPayloads.Download(VideoPath), queryToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        var call = Assert.Single(scan.Imports);
        Assert.Equal(VideoPath, call.Path);
    }

    [Fact]
    public async Task Download_DeliveredTwice_IngestsOnce_SecondIsDuplicateNoOp()
    {
        // At-least-once webhooks + poll overlap WILL redeliver the same import; the ledger dedups it.
        var store = await StoreWithSecret();
        var (receiver, scan) = NewReceiver(store);
        var body = WebhookPayloads.Download(VideoPath, downloadId: "DL-DUP");

        var first = await receiver.HandleAsync(Context(body, headerToken: Secret), default);
        var second = await receiver.HandleAsync(Context(body, headerToken: Secret), default);

        Assert.Equal(200, StatusOf(first));
        Assert.Equal(200, StatusOf(second));
        Assert.Single(scan.Imports); // the second delivery ingested nothing
    }

    [Fact]
    public async Task Download_IsUpgrade_WithConfirmedMatch_UpgradesInPlaceWithExistingCoveId()
    {
        // An upgrade re-import of an already-matched movie passes its Cove id so ImportDownloaded* upgrades
        // in place rather than creating a second entity (IMPT-03 / Pitfall 3).
        var store = await StoreWithSecret();
        await new MatchStateStore(store).ConfirmAsync(new MatchState(
            CoveId: 55, WhisparrMovieId: 7, StashId: "uuid-x", MatchedBy: MatchedBy.StashId,
            MatchedAtUtcTicks: 638_000_000_000_000_000L, Status: MatchStatus.Confirmed));
        var (receiver, scan) = NewReceiver(store);

        var body = WebhookPayloads.Download(VideoPath, downloadId: "DL-UP", isUpgrade: true, movieId: 7);
        var result = await receiver.HandleAsync(Context(body, headerToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        var call = Assert.Single(scan.Imports);
        Assert.Equal(55, call.EntityId);
    }
}
