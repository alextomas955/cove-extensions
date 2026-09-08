using System.Net;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;
using V2Api = Whisparr2.Net.Api;
using V3Api = Whisparr3.Net.Api;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// What a gateway holds per instance, on each generation.
/// </summary>
/// <remarks>
/// Each case asserts what a caller can observe: how many primary handlers the registration asked the
/// supplied factory for, and whether a provider a discarded registration handed out still answers. A
/// case reading the cache itself would pass on a gateway that held one registration and used none of
/// it. Both generations are covered case for case, because the bookkeeping is one type and a case
/// running on one generation would leave the other's use of it untested.
/// </remarks>
public sealed class GatewayRegistrationTests
{
    private const string SomeKey = "0123456789abcdef0123456789abcdef";
    private const string OtherKey = "fedcba9876543210fedcba9876543210";

    private static readonly Uri SomeAddress = new("http://whisparr:6969");

    [Fact]
    public async Task APairReachedTwiceAsksForNoSecondHandlerOnTheOlderGeneration()
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
    public async Task APairReachedTwiceAsksForNoSecondHandlerOnTheNewerGeneration()
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

    /// <summary>A key edited against the same address is a registration of its own.</summary>
    /// <remarks>
    /// The pair is the key, so a request after a key change cannot travel through a registration bound
    /// to the credential the person replaced.
    /// </remarks>
    [Fact]
    public async Task AKeyChangedAgainstTheSameAddressAsksForAnotherHandlerOnBothGenerations()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}");
        var askedForByTheOlder = 0;
        var askedForByTheNewer = 0;
        using var older = new Whisparr2Gateway(() =>
        {
            askedForByTheOlder++;
            return handler;
        });
        using var newer = new Whisparr3Gateway(() =>
        {
            askedForByTheNewer++;
            return handler;
        });

        await ReadThroughAsync(older, SomeAddress, SomeKey);
        var olderAfterFirst = askedForByTheOlder;
        await ReadThroughAsync(older, SomeAddress, OtherKey);

        await ReadThroughAsync(newer, SomeAddress, SomeKey);
        var newerAfterFirst = askedForByTheNewer;
        await ReadThroughAsync(newer, SomeAddress, OtherKey);

        Assert.True(
            askedForByTheOlder > olderAfterFirst,
            "the older generation reused a registration bound to the key that was replaced");
        Assert.True(
            askedForByTheNewer > newerAfterFirst,
            "the newer generation reused a registration bound to the key that was replaced");
    }

    [Fact]
    public void ANinthPairDiscardsTheLeastRecentlyReachedOnTheOlderGeneration()
    {
        using var gateway = new Whisparr2Gateway();

        var discarded = gateway.For(new Whisparr2Target(AddressNumbered(1), SomeKey));
        for (var pair = 2; pair <= 9; pair++)
        {
            gateway.For(new Whisparr2Target(AddressNumbered(pair), SomeKey));
        }

        var held = gateway.For(new Whisparr2Target(AddressNumbered(9), SomeKey));
        Assert.Throws<ObjectDisposedException>(() => discarded.Api<V2Api.IHistoryApi>());
        Assert.NotNull(held.Api<V2Api.IHistoryApi>());
    }

    [Fact]
    public void ANinthPairDiscardsTheLeastRecentlyReachedOnTheNewerGeneration()
    {
        using var gateway = new Whisparr3Gateway();

        var discarded = gateway.For(new Whisparr3Target(AddressNumbered(1), SomeKey));
        for (var pair = 2; pair <= 9; pair++)
        {
            gateway.For(new Whisparr3Target(AddressNumbered(pair), SomeKey));
        }

        var held = gateway.For(new Whisparr3Target(AddressNumbered(9), SomeKey));
        Assert.Throws<ObjectDisposedException>(() => discarded.Api<V3Api.IHistoryApi>());
        Assert.NotNull(held.Api<V3Api.IHistoryApi>());
    }

    [Fact]
    public void DisposalDiscardsEveryRegistrationAndRefusesAFurtherReachOnTheOlderGeneration()
    {
        var gateway = new Whisparr2Gateway();
        var first = gateway.For(new Whisparr2Target(AddressNumbered(1), SomeKey));
        var second = gateway.For(new Whisparr2Target(AddressNumbered(2), SomeKey));

        gateway.Dispose();

        Assert.Throws<ObjectDisposedException>(() => first.Api<V2Api.IHistoryApi>());
        Assert.Throws<ObjectDisposedException>(() => second.Api<V2Api.IHistoryApi>());
        Assert.Throws<ObjectDisposedException>(
            () => gateway.For(new Whisparr2Target(AddressNumbered(3), SomeKey)));
    }

    [Fact]
    public void DisposalDiscardsEveryRegistrationAndRefusesAFurtherReachOnTheNewerGeneration()
    {
        var gateway = new Whisparr3Gateway();
        var first = gateway.For(new Whisparr3Target(AddressNumbered(1), SomeKey));
        var second = gateway.For(new Whisparr3Target(AddressNumbered(2), SomeKey));

        gateway.Dispose();

        Assert.Throws<ObjectDisposedException>(() => first.Api<V3Api.IHistoryApi>());
        Assert.Throws<ObjectDisposedException>(() => second.Api<V3Api.IHistoryApi>());
        Assert.Throws<ObjectDisposedException>(
            () => gateway.For(new Whisparr3Target(AddressNumbered(3), SomeKey)));
    }

    private static Uri AddressNumbered(int pair) => new($"http://whisparr{pair}:6969");

    private static Task<V2Api.IGetHistoryApiResponse> ReadThroughAsync(
        Whisparr2Gateway gateway, Uri address, string apiKey)
        => gateway.For(new Whisparr2Target(address, apiKey))
            .Api<V2Api.IHistoryApi>()
            .GetHistoryAsync(cancellationToken: TestContext.Current.CancellationToken);

    private static Task<V3Api.IGetHistoryApiResponse> ReadThroughAsync(
        Whisparr3Gateway gateway, Uri address, string apiKey)
        => gateway.For(new Whisparr3Target(address, apiKey))
            .Api<V3Api.IHistoryApi>()
            .GetHistoryAsync(cancellationToken: TestContext.Current.CancellationToken);
}
