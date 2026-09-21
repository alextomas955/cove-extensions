using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;
using V2Api = Whisparr2.Net.Api;

namespace WhisparrSync.Tests.Whisparr;

// The budgets here are milliseconds so a case finishes. What is under test is that the number the
// target names is the one the request is bounded by, not the size of either shipped number.
public sealed class GatewayBudgetTests
{
    private const string SomeKey = "0123456789abcdef0123456789abcdef";
    private const string SomeSiteIdentifier = "a30bc641-6afe-4c80-9c73-ecb68104a68d";

    private static readonly Uri SomeAddress = new("http://whisparr:6969");
    private static readonly TimeSpan LongerThanTheSlowAnswer = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShorterThanTheSlowAnswer = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TheSlowAnswer = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task AnAnswerSlowerThanTheTargetsBudgetIsGivenUpOn()
    {
        using var gateway = new Whisparr2Gateway(() => new SlowHandler(TheSlowAnswer));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadThroughAsync(gateway, ShorterThanTheSlowAnswer));
    }

    [Fact]
    public async Task TheSameAnswerIsWaitedForWhereTheTargetAsksForLonger()
    {
        using var gateway = new Whisparr2Gateway(() => new SlowHandler(TheSlowAnswer));

        var answered = await ReadThroughAsync(gateway, LongerThanTheSlowAnswer);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
    }

    [Fact]
    public void AReadOfEverythingHeldIsBudgetedAboveAPerItemCall()
        => Assert.True(WhisparrClient.LibraryReadTimeout > WhisparrClient.RequestTimeout);

    // Driving the real timeout would mean waiting out the per-item budget, so the case records the
    // budget each registration was built with, which is the value the request is bounded by.
    [Fact]
    public async Task AskingWhichSitesAreHeldIsBudgetedForAReadOfEverythingHeld()
    {
        var builtWith = new List<TimeSpan>();
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var http = new HttpClient(handler);
        WhisparrClient.Configure(http);
        var client = new WhisparrClient(
            http,
            new Whisparr3Gateway(() => handler, c => c.Timeout = http.Timeout),
            new Whisparr2Gateway(() => handler, c => builtWith.Add(c.Timeout)),
            new TestSiteNumbers(),
            NullLogger.Instance);

        await client.ReduceHeldSitesAsync(
            SomeAddress, SomeKey, [207], TestContext.Current.CancellationToken);

        Assert.Contains(WhisparrClient.LibraryReadTimeout, builtWith);
        Assert.DoesNotContain(WhisparrClient.RequestTimeout, builtWith);
    }

    // Narrowing the read by the site's own number bounds how much comes back, not how long it takes:
    // this generation builds its whole set before filtering, so one site arrives no sooner than the
    // whole list does.
    [Fact]
    public async Task ReadingWhetherOneSiteIsHeldIsBudgetedForAReadOfEverythingHeld()
    {
        var builtWith = new List<TimeSpan>();
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var http = new HttpClient(handler);
        WhisparrClient.Configure(http);
        var client = new WhisparrClient(
            http,
            new Whisparr3Gateway(() => handler, c => c.Timeout = http.Timeout),
            new Whisparr2Gateway(() => handler, c => builtWith.Add(c.Timeout)),
            TestSiteNumbers.Numbering(SomeSiteIdentifier, 207),
            NullLogger.Instance);

        await client.ReadStudioAsync(
            SomeAddress,
            SomeKey,
            WhisparrGeneration.V2,
            SomeSiteIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Contains(WhisparrClient.LibraryReadTimeout, builtWith);
        Assert.DoesNotContain(WhisparrClient.RequestTimeout, builtWith);
    }

    private static async Task<V2Api.IGetHistoryApiResponse> ReadThroughAsync(
        Whisparr2Gateway gateway, TimeSpan budget)
    {
        using var apis = gateway.For(new Whisparr2Target(SomeAddress, SomeKey, budget));
        return await apis
            .Api<V2Api.IHistoryApi>()
            .GetHistoryAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
