using System.Net;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;
using V2Api = Whisparr2.Net.Api;
using V3Api = Whisparr3.Net.Api;

namespace WhisparrSync.Tests.Whisparr;

// Each case asserts what a caller can observe: how many handlers the registration asked the factory
// for, and whether a provider from a discarded registration still answers. A case reading the cache
// itself would pass on a gateway that held one registration and used none of it.
public sealed class GatewayRegistrationTests
{
    private const string SomeKey = "0123456789abcdef0123456789abcdef";
    private const string OtherKey = "fedcba9876543210fedcba9876543210";

    private static readonly Uri SomeAddress = new("http://whisparr:6969");

    [Fact]
    public async Task APairReachedTwiceAsksForNoSecondHandlerOnV2()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}");
        var askedFor = 0;
        using var gateway = new Whisparr2Gateway(() =>
        {
            askedFor++;
            return handler;
        });

        await ReadThroughAsync(gateway, SomeAddress, SomeKey);
        var afterFirst = askedFor;
        await ReadThroughAsync(gateway, SomeAddress, SomeKey);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(afterFirst, askedFor);
    }

    [Fact]
    public async Task APairReachedTwiceAsksForNoSecondHandlerOnV3()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}");
        var askedFor = 0;
        using var gateway = new Whisparr3Gateway(() =>
        {
            askedFor++;
            return handler;
        });

        await ReadThroughAsync(gateway, SomeAddress, SomeKey);
        var afterFirst = askedFor;
        await ReadThroughAsync(gateway, SomeAddress, SomeKey);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(afterFirst, askedFor);
    }

    // The address and key together are the cache key, so a request after a key change cannot travel
    // through a registration bound to the replaced credential.
    [Fact]
    public async Task AKeyChangedAgainstTheSameAddressAsksForAnotherHandlerOnBothGenerations()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}");
        var askedForByV2 = 0;
        var askedForByV3 = 0;
        using var v2Gateway = new Whisparr2Gateway(() =>
        {
            askedForByV2++;
            return handler;
        });
        using var v3Gateway = new Whisparr3Gateway(() =>
        {
            askedForByV3++;
            return handler;
        });

        await ReadThroughAsync(v2Gateway, SomeAddress, SomeKey);
        var v2AfterFirst = askedForByV2;
        await ReadThroughAsync(v2Gateway, SomeAddress, OtherKey);

        await ReadThroughAsync(v3Gateway, SomeAddress, SomeKey);
        var v3AfterFirst = askedForByV3;
        await ReadThroughAsync(v3Gateway, SomeAddress, OtherKey);

        Assert.True(
            askedForByV2 > v2AfterFirst,
            "v2 reused a registration bound to the key that was replaced");
        Assert.True(
            askedForByV3 > v3AfterFirst,
            "v3 reused a registration bound to the key that was replaced");
    }

    // A discarded pair reached again is registered from nothing, so it asks the factory for another
    // handler. Counting handlers reads the cache the way a request does.
    [Fact]
    public async Task ANinthPairDiscardsTheLeastRecentlyReachedOnV2()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}");
        var askedFor = 0;
        using var gateway = new Whisparr2Gateway(() =>
        {
            askedFor++;
            return handler;
        });

        for (var pair = 1; pair <= 9; pair++)
        {
            await ReadThroughAsync(gateway, AddressNumbered(pair), SomeKey);
        }

        var afterNinePairs = askedFor;
        await ReadThroughAsync(gateway, AddressNumbered(1), SomeKey);

        Assert.True(askedFor > afterNinePairs, "the least recently reached pair was kept");
    }

    [Fact]
    public async Task ANinthPairDiscardsTheLeastRecentlyReachedOnV3()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}");
        var askedFor = 0;
        using var gateway = new Whisparr3Gateway(() =>
        {
            askedFor++;
            return handler;
        });

        for (var pair = 1; pair <= 9; pair++)
        {
            await ReadThroughAsync(gateway, AddressNumbered(pair), SomeKey);
        }

        var afterNinePairs = askedFor;
        await ReadThroughAsync(gateway, AddressNumbered(1), SomeKey);

        Assert.True(askedFor > afterNinePairs, "the least recently reached pair was kept");
    }

    // The cap may not discard a registration a caller is still holding: that caller resolves its api
    // after the reach, and its request sends after that again. One registry serves both gateways, so
    // the lifetime is covered once.
    [Fact]
    public void ARegistrationAPairIsHoldingOutlivesTheCap()
    {
        using var gateway = new Whisparr3Gateway();

        using var held = gateway.For(new Whisparr3Target(AddressNumbered(1), SomeKey));
        for (var pair = 2; pair <= 9; pair++)
        {
            gateway.For(new Whisparr3Target(AddressNumbered(pair), SomeKey)).Dispose();
        }

        Assert.NotNull(held.Api<V3Api.IHistoryApi>());
    }

    // Each pair is released first, as a finished request releases it. What the caller keeps is then a
    // spent handle, and that handle answering nothing is how disposal shows it discarded the provider
    // rather than leaking it.
    [Fact]
    public void DisposalDiscardsEveryRegistrationAndRefusesAFurtherReachOnV2()
    {
        var gateway = new Whisparr2Gateway();
        var first = gateway.For(new Whisparr2Target(AddressNumbered(1), SomeKey));
        var second = gateway.For(new Whisparr2Target(AddressNumbered(2), SomeKey));
        first.Dispose();
        second.Dispose();

        gateway.Dispose();

        Assert.Throws<ObjectDisposedException>(() => first.Api<V2Api.IHistoryApi>());
        Assert.Throws<ObjectDisposedException>(() => second.Api<V2Api.IHistoryApi>());
        Assert.Throws<ObjectDisposedException>(
            () => gateway.For(new Whisparr2Target(AddressNumbered(3), SomeKey)));
    }

    [Fact]
    public void DisposalDiscardsEveryRegistrationAndRefusesAFurtherReachOnV3()
    {
        var gateway = new Whisparr3Gateway();
        var first = gateway.For(new Whisparr3Target(AddressNumbered(1), SomeKey));
        var second = gateway.For(new Whisparr3Target(AddressNumbered(2), SomeKey));
        first.Dispose();
        second.Dispose();

        gateway.Dispose();

        Assert.Throws<ObjectDisposedException>(() => first.Api<V3Api.IHistoryApi>());
        Assert.Throws<ObjectDisposedException>(() => second.Api<V3Api.IHistoryApi>());
        Assert.Throws<ObjectDisposedException>(
            () => gateway.For(new Whisparr3Target(AddressNumbered(3), SomeKey)));
    }

    private static Uri AddressNumbered(int pair) => new($"http://whisparr{pair}:6969");

    private static async Task<V2Api.IGetHistoryApiResponse> ReadThroughAsync(
        Whisparr2Gateway gateway, Uri address, string apiKey)
    {
        using var apis = gateway.For(new Whisparr2Target(address, apiKey));
        return await apis
            .Api<V2Api.IHistoryApi>()
            .GetHistoryAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<V3Api.IGetHistoryApiResponse> ReadThroughAsync(
        Whisparr3Gateway gateway, Uri address, string apiKey)
    {
        using var apis = gateway.For(new Whisparr3Target(address, apiKey));
        return await apis
            .Api<V3Api.IHistoryApi>()
            .GetHistoryAsync(cancellationToken: TestContext.Current.CancellationToken);
    }
}
