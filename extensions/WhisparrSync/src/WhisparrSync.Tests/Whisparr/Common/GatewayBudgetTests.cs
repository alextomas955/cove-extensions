using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;
using V2Api = Whisparr2.Net.Api;
using V3Api = Whisparr3.Net.Api;

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

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // Both arms, over a gateway built the way ConfigureServices builds one. Driven on one arm alone
    // this passed while the other discarded the budget and bounded every read at the per-item one.
    [Theory]
    [InlineData(WhisparrGeneration.V2)]
    [InlineData(WhisparrGeneration.V3)]
    public async Task AnAnswerSlowerThanTheTargetsBudgetIsGivenUpOn(WhisparrGeneration generation)
    {
        using var arm = GatewayArm.For(generation, new SlowHandler(TheSlowAnswer));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => arm.ReadAsync(ShorterThanTheSlowAnswer, TestCt));
    }

    [Theory]
    [InlineData(WhisparrGeneration.V2)]
    [InlineData(WhisparrGeneration.V3)]
    public async Task TheSameAnswerIsWaitedForWhereTheTargetAsksForLonger(
        WhisparrGeneration generation)
    {
        using var arm = GatewayArm.For(generation, new SlowHandler(TheSlowAnswer));

        Assert.Equal(
            HttpStatusCode.OK, await arm.ReadAsync(LongerThanTheSlowAnswer, TestCt));
    }

    [Fact]
    public void AReadOfEverythingHeldIsBudgetedAboveAPerItemCall()
        => Assert.True(WhisparrTransport.LibraryReadTimeout > WhisparrTransport.RequestTimeout);

    // Driving the real timeout would mean waiting out the per-item budget, so the case records the
    // budget each registration was built with, which is the value the request is bounded by.
    [Fact]
    public async Task AskingWhichSitesAreHeldIsBudgetedForAReadOfEverythingHeld()
    {
        var builtWith = new List<TimeSpan>();
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var http = new HttpClient(handler);
        WhisparrTransport.Configure(http);
        var client = V2Over(http, handler, builtWith);

        await ((IWhisparrHeldSiteReading)client)
            .ReduceHeldSitesAsync([207], TestContext.Current.CancellationToken);

        Assert.Contains(WhisparrTransport.LibraryReadTimeout, builtWith);
        Assert.DoesNotContain(WhisparrTransport.RequestTimeout, builtWith);
    }

    // The lookup answers one site without the pass over every site the list route makes, so it is
    // bounded as the per-item call it is. Budgeted for a read of everything held it would wait
    // minutes on an instance that had already stopped answering.
    [Fact]
    public async Task ReadingWhetherOneSiteIsHeldIsBudgetedAsAPerItemCall()
    {
        var builtWith = new List<TimeSpan>();
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var http = new HttpClient(handler);
        WhisparrTransport.Configure(http);
        var client = V2Over(http, handler, builtWith);

        await ((IWhisparrStudioActing)client).ReadStudioAsync(
            SomeSiteIdentifier, TestContext.Current.CancellationToken);

        Assert.Contains(WhisparrTransport.RequestTimeout, builtWith);
        Assert.DoesNotContain(WhisparrTransport.LibraryReadTimeout, builtWith);
    }

    // The budget the v2 arm builds its client with is what a case reads off builtWith.
    private static IWhisparrClient V2Over(
        HttpClient http, HttpMessageHandler handler, List<TimeSpan> builtWith)
    {
        var v3Gateway = new Whisparr3Gateway(() => handler, c => c.Timeout = http.Timeout);
        return new WhisparrInstanceFactory(
                new WhisparrTransport(http, v3Gateway, NullLogger.Instance),
                v3Gateway,
                new Whisparr2Gateway(() => handler, c => builtWith.Add(c.Timeout)),
                new TestSiteNumbers(),
                NullLogger.Instance)
            .Bound(new WhisparrBinding(WhisparrGeneration.V2, SomeAddress, SomeKey));
    }

    // One read per generation through a default-constructed gateway, so what bounds the send is the
    // target's budget and nothing a case supplied.
    private sealed class GatewayArm(IDisposable gateway, Func<TimeSpan, CancellationToken, Task<HttpStatusCode>> read)
        : IDisposable
    {
        public static GatewayArm For(WhisparrGeneration generation, HttpMessageHandler handler)
        {
            if (generation is WhisparrGeneration.V2)
            {
                var v2 = new Whisparr2Gateway(() => handler);
                return new GatewayArm(v2, async (budget, ct) =>
                {
                    using var apis = v2.For(new Whisparr2Target(SomeAddress, SomeKey, budget));
                    return (await apis.Api<V2Api.IHistoryApi>()
                        .GetHistoryAsync(cancellationToken: ct)).StatusCode;
                });
            }

            var v3 = new Whisparr3Gateway(() => handler);
            return new GatewayArm(v3, async (budget, ct) =>
            {
                using var apis = v3.For(new Whisparr3Target(SomeAddress, SomeKey, budget));
                return (await apis.Api<V3Api.IRootFolderApi>().GetRootfolderAsync(ct)).StatusCode;
            });
        }

        public Task<HttpStatusCode> ReadAsync(TimeSpan budget, CancellationToken ct)
            => read(budget, ct);

        public void Dispose() => gateway.Dispose();
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
