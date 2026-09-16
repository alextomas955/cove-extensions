using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;
using V2Api = Whisparr2.Net.Api;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// How long a call through the older generation's gateway is allowed to take.
/// </summary>
/// <remarks>
/// A read of everything an instance holds waits on the instance building all of it, which a per-item
/// budget cannot cover. The cases below drive the budget rather than reading it back: a client whose
/// timeout never reached it answers a slow instance the same way whatever the target asked for.
/// <para>
/// The budgets here are milliseconds so a case finishes. What is under test is that the number the
/// target names is the one the request is bounded by, not the size of either shipped number.
/// </para>
/// </remarks>
public sealed class GatewayBudgetTests
{
    private const string SomeKey = "0123456789abcdef0123456789abcdef";

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

    /// <summary>The same answer, the same instance, and only the budget different.</summary>
    [Fact]
    public async Task TheSameAnswerIsWaitedForWhereTheTargetAsksForLonger()
    {
        using var gateway = new Whisparr2Gateway(() => new SlowHandler(TheSlowAnswer));

        var answered = await ReadThroughAsync(gateway, LongerThanTheSlowAnswer);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
    }

    /// <summary>A read of everything the instance holds is the one given the longer budget.</summary>
    [Fact]
    public void AReadOfEverythingHeldIsBudgetedAboveAPerItemCall()
        => Assert.True(WhisparrClient.LibraryReadTimeout > WhisparrClient.RequestTimeout);

    /// <summary>
    /// The read that asks which sites the instance holds is made against the longer budget.
    /// </summary>
    /// <remarks>
    /// Driving the real timeout would mean a case that waits out the per-item budget. What is
    /// recorded instead is the budget each registration was built with, which is the value the
    /// request is bounded by: a client built at the per-item budget gives up on this instance at
    /// fifteen seconds whatever the call site meant.
    /// </remarks>
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

    private static Task<V2Api.IGetHistoryApiResponse> ReadThroughAsync(
        Whisparr2Gateway gateway, TimeSpan budget)
        => gateway.For(new Whisparr2Target(SomeAddress, SomeKey, budget))
            .Api<V2Api.IHistoryApi>()
            .GetHistoryAsync(cancellationToken: TestContext.Current.CancellationToken);

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
