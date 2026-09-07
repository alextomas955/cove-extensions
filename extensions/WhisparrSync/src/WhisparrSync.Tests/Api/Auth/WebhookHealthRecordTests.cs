using System.Text;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Ingest;
using WhisparrSync.Options;
using WhisparrSync.State;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Api.Auth;

/// <summary>
/// The fail-closed <c>/webhook</c> 401 at the endpoint boundary, and the one thing it now remembers. Two states
/// share that single return and must not record the same thing: a mismatch against a CONFIGURED secret is a
/// genuine rejection worth keeping, while a delivery arriving before any secret has been minted must record
/// nothing at all — the route is anonymous, so an unauthenticated stranger could otherwise plant a sticky
/// import-channel failure on a fresh install. The assertions read the PERSISTED blob, not the response body:
/// the point is that the outcome is now remembered.
/// </summary>
[Trait("Tier", "L2")]
public sealed class WebhookHealthRecordTests
{
    private const string StoredSecret = "SENTINEL-STORED-SECRET-4b1c8e02";
    private const string PresentedToken = "SENTINEL-PRESENTED-TOKEN-9d7f3a15";
    private const string HealthKey = "health";

    private static async Task<FakeStore> StoreWithSecret(string? secret)
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(new WhisparrOptions { WebhookSecret = secret ?? "" });
        return store;
    }

    private static WebhookReceiver NewReceiver(FakeStore store)
    {
        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => new FakeScanService());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var coordinator = new IngestCoordinator(
            scopeFactory, _ => ValueTask.FromResult<IReadOnlyList<string>>(["/data/media"]));
        return new WebhookReceiver(store, coordinator);
    }

    private static DefaultHttpContext Context(string? headerToken)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"eventType\":\"Test\"}"));
        if (headerToken is not null)
        {
            http.Request.Headers["X-Cove-Token"] = headerToken;
        }

        return http;
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    [Fact]
    public async Task WrongToken_AgainstAConfiguredSecret_Returns401_AndRecordsAnImportFailure()
    {
        var store = await StoreWithSecret(StoredSecret);

        var result = await NewReceiver(store).HandleAsync(Context(PresentedToken), default);

        Assert.Equal(401, StatusOf(result));
        var entry = Assert.Single(
            await new HealthStore(store).LoadAsync(), e => e.Dependency == HealthDependency.Import);
        Assert.Equal(1, entry.ConsecutiveFailures);
        Assert.NotEmpty(entry.LastError);

        var blob = await store.GetAsync(HealthKey);
        Assert.NotNull(blob);
        Assert.DoesNotContain(StoredSecret, blob, StringComparison.Ordinal);
        Assert.DoesNotContain(PresentedToken, blob, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoToken_AgainstAConfiguredSecret_BehavesIdentically()
    {
        var store = await StoreWithSecret(StoredSecret);

        var result = await NewReceiver(store).HandleAsync(Context(headerToken: null), default);

        Assert.Equal(401, StatusOf(result));
        var entry = Assert.Single(
            await new HealthStore(store).LoadAsync(), e => e.Dependency == HealthDependency.Import);
        Assert.Equal(1, entry.ConsecutiveFailures);

        var blob = await store.GetAsync(HealthKey);
        Assert.NotNull(blob);
        Assert.DoesNotContain(StoredSecret, blob, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSecretConfigured_Returns401_AndLeavesTheHealthBlobByteIdentical()
    {
        var store = await StoreWithSecret(secret: null);
        var before = await store.GetAsync(HealthKey);

        var result = await NewReceiver(store).HandleAsync(Context(PresentedToken), default);

        Assert.Equal(401, StatusOf(result));
        // Byte-identical, and absent stays absent: an anonymous caller cannot force even a store round-trip on
        // a never-configured install, let alone plant a persistent false alarm.
        Assert.Equal(before, await store.GetAsync(HealthKey));
        Assert.Empty(await new HealthStore(store).LoadAsync());
    }
}
