using System.Text;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Ingest;
using WhisparrSync.Matching;
using WhisparrSync.Options;
using WhisparrSync.State;
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

    private static (WebhookReceiver Receiver, FakeScanService Scan) NewReceiver(FakeStore store, params string[] roots)
        => NewReceiver(store, onQueryTokenFallback: null, roots);

    private static (WebhookReceiver Receiver, FakeScanService Scan) NewReceiver(
        FakeStore store, Action? onQueryTokenFallback, params string[] roots)
    {
        roots = roots.Length == 0 ? ["/data/media"] : roots; // the default root contains VideoPath
        var scan = new FakeScanService();
        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => scan);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var coordinator = new IngestCoordinator(
            scopeFactory, _ => ValueTask.FromResult<IReadOnlyList<string>>(roots));
        return (new WebhookReceiver(store, coordinator, onQueryTokenFallback), scan);
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
    public async Task QueryToken_Fallback_IsAccepted_ButWarnsOnce()
    {
        // WR-03: the ?token= query still authenticates (hand-pasted webhook fallback), but because a secret in
        // a URL query leaks to proxy/access logs, the receiver signals the host so it can warn about the risk.
        var store = await StoreWithSecret();
        var warnings = 0;
        var (receiver, scan) = NewReceiver(store, onQueryTokenFallback: () => warnings++);

        var result = await receiver.HandleAsync(Context(WebhookPayloads.Download(VideoPath), queryToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(VideoPath, Assert.Single(scan.Imports).Path);
        Assert.Equal(1, warnings); // the query-token channel fired the log-exposure warning
    }

    [Fact]
    public async Task HeaderToken_IsPreferred_AndDoesNotWarn()
    {
        // The header is the preferred channel: a header-authenticated request never triggers the WR-03 warning,
        // even when a query token is also present (the header is checked first).
        var store = await StoreWithSecret();
        var warnings = 0;
        var (receiver, _) = NewReceiver(store, onQueryTokenFallback: () => warnings++);

        var ctx = Context(WebhookPayloads.Download(VideoPath), headerToken: Secret, queryToken: Secret);
        var result = await receiver.HandleAsync(ctx, default);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(0, warnings); // authenticated on the header — no query-channel warning
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

    [Fact]
    public async Task Download_Imported_WritesOneImportLogEntry()
    {
        var store = await StoreWithSecret();
        var (receiver, _) = NewReceiver(store);

        await receiver.HandleAsync(Context(WebhookPayloads.Download(VideoPath, downloadId: "DL-1"), headerToken: Secret), default);

        var entry = Assert.Single(await new ImportLog(store).LoadAllAsync());
        Assert.Equal("webhook", entry.Source);
        Assert.Equal("Download", entry.EventType);
        Assert.Equal(VideoPath, entry.Path);
        Assert.Equal("Video", entry.Kind);
        Assert.Equal("Imported", entry.Result);
        Assert.True(entry.UtcTicks > 0); // server-written, never a browser value
        Assert.Equal(EventLedger.ImportKey("DL-1", VideoPath), entry.LedgerKey);
    }

    [Fact]
    public async Task Download_Duplicate_WritesSkippedEntry()
    {
        var store = await StoreWithSecret();
        var (receiver, _) = NewReceiver(store);
        var body = WebhookPayloads.Download(VideoPath, downloadId: "DL-DUP2");

        await receiver.HandleAsync(Context(body, headerToken: Secret), default);
        await receiver.HandleAsync(Context(body, headerToken: Secret), default);

        var all = await new ImportLog(store).LoadAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("Imported", all[0].Result);
        Assert.Equal("Skipped", all[1].Result);
    }

    [Fact]
    public async Task Download_OutOfRoot_WritesFlaggedEntry_AndDoesNotIngest()
    {
        var store = await StoreWithSecret();
        var (receiver, scan) = NewReceiver(store, "/some/other/root");

        var result = await receiver.HandleAsync(
            Context(WebhookPayloads.Download(VideoPath, downloadId: "DL-OOR"), headerToken: Secret), default);

        Assert.Equal(200, StatusOf(result)); // the receiver always answers 200 to an authenticated event
        Assert.Empty(scan.Imports);
        Assert.Empty(scan.Scans);
        var entry = Assert.Single(await new ImportLog(store).LoadAllAsync());
        Assert.Equal("Flagged", entry.Result);
        Assert.Contains("outside", entry.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Whisparr v2 (Sonarr-shaped) On-Import: the version-blind receiver ingests episodeFile.path exactly
    //    like the v3 movieFile.path, through the SAME token gate / ledger / audit / root guard / coordinator.

    [Fact]
    public async Task ValidToken_DownloadV2_IngestsTheEpisodeFilePathAsVideo()
    {
        // VER-03: a v2 On-Download (series + episodes[] + episodeFile) ingests episodeFile.path via IScanService
        // exactly like the v3 movieFile case — the path fallback is version-blind.
        var (receiver, scan) = NewReceiver(await StoreWithSecret());

        var result = await receiver.HandleAsync(Context(WebhookPayloads.DownloadV2(VideoPath), headerToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        var call = Assert.Single(scan.Imports);
        Assert.Equal("Video", call.Kind);
        Assert.Equal(VideoPath, call.Path);
    }

    [Fact]
    public async Task DownloadV2_IsUpgrade_WithConfirmedMatch_UpgradesInPlaceWithExistingCoveId()
    {
        // A v2 upgrade resolves the existing Cove id from the EPISODE id (v2's match handle = WhisparrMovieId
        // per 04-01), so ImportDownloaded* upgrades in place rather than creating a second entity.
        var store = await StoreWithSecret();
        await new MatchStateStore(store).ConfirmAsync(new MatchState(
            CoveId: 55, WhisparrMovieId: 7, StashId: "uuid-x", MatchedBy: MatchedBy.StashId,
            MatchedAtUtcTicks: 638_000_000_000_000_000L, Status: MatchStatus.Confirmed));
        var (receiver, scan) = NewReceiver(store);

        var body = WebhookPayloads.DownloadV2(VideoPath, downloadId: "V2-UP", isUpgrade: true, episodeId: 7);
        var result = await receiver.HandleAsync(Context(body, headerToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        var call = Assert.Single(scan.Imports);
        Assert.Equal(55, call.EntityId);
    }

    [Fact]
    public async Task DownloadV2_OutOfRoot_WritesFlaggedEntry_AndDoesNotIngest()
    {
        // The v2 imported path is subject to the SAME reused root guard — an out-of-root path is Flagged, never ingested.
        var store = await StoreWithSecret();
        var (receiver, scan) = NewReceiver(store, "/some/other/root");

        var result = await receiver.HandleAsync(
            Context(WebhookPayloads.DownloadV2(VideoPath, downloadId: "V2-OOR"), headerToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        Assert.Empty(scan.Imports);
        Assert.Empty(scan.Scans);
        var entry = Assert.Single(await new ImportLog(store).LoadAllAsync());
        Assert.Equal("Flagged", entry.Result);
        Assert.Contains("outside", entry.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedV2Body_Is200NoOp_AndDoesNotIngest()
    {
        // Schema drift / a truncated v2 body must degrade to a 200 no-op (never a 500, never a half-ingest).
        var (receiver, scan) = NewReceiver(await StoreWithSecret());

        var result = await receiver.HandleAsync(
            Context("{ \"eventType\": \"Download\", \"episodeFile\": { \"path\": ", headerToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        Assert.Empty(scan.Imports);
    }

    [Fact]
    public async Task DownloadV2_WithNoPathAnywhere_Is200NoOp_AndDoesNotIngest()
    {
        // A well-formed v2 Download carrying no importable path is a safe 200 no-op — nothing to ingest, nothing audited.
        var store = await StoreWithSecret();
        var (receiver, scan) = NewReceiver(store);

        var result = await receiver.HandleAsync(
            Context(WebhookPayloads.DownloadV2(path: string.Empty, downloadId: "V2-NOPATH"), headerToken: Secret), default);

        Assert.Equal(200, StatusOf(result));
        Assert.Empty(scan.Imports);
        Assert.Empty(await new ImportLog(store).LoadAllAsync()); // no-path returns before claim/audit — no ledger, no log entry
    }
}
