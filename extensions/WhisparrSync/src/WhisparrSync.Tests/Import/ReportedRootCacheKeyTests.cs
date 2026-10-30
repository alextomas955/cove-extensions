using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Import;

// The roots a held entry names are one instance's own hand-created paths. An entry answering for an
// instance it was not read from sends the next delivery's file to a root the new instance does not
// declare.
public sealed class ReportedRootCacheKeyTests
{
    private const string FirstAddress = "http://whisparr-first:6969";
    private const string SecondAddress = "http://whisparr-second:6969";
    private const string Key = "2a4a2a4a2a4a2a4a2a4a2a4a2a4a2a4a";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASecondReadAtADifferentAddressAnswersThatInstancesOwnRoots()
    {
        var instances = new RootsPerHost();
        var credentials = new RecordingCredentialPort()
            .Holding(WhisparrGeneration.V3, FirstAddress, Key);
        var port = PortOver(instances, credentials);

        await port.ReadAsync(WhisparrGeneration.V3, TestCt);
        credentials.Holding(WhisparrGeneration.V3, SecondAddress, Key);
        var second = await port.ReadAsync(WhisparrGeneration.V3, TestCt);

        Assert.Equal(["/media/second"], second);
        Assert.Equal(
            ["whisparr-first", "whisparr-second"],
            instances.Bindings.Select(binding => binding.BaseAddress.Host));
    }

    // The control for the case above: a held entry still spares the instance a request, which is
    // what this cache exists for.
    [Fact]
    public async Task ASecondReadAtTheSameAddressIsAnsweredWithoutReachingTheInstance()
    {
        var instances = new RootsPerHost();
        var credentials = new RecordingCredentialPort()
            .Holding(WhisparrGeneration.V3, FirstAddress, Key);
        var port = PortOver(instances, credentials);

        await port.ReadAsync(WhisparrGeneration.V3, TestCt);
        var second = await port.ReadAsync(WhisparrGeneration.V3, TestCt);

        Assert.Equal(["/media/first"], second);
        Assert.Single(instances.Bindings);
    }

    // The unconfigured reading is about nothing, so it must not answer for an address configured
    // afterwards.
    [Fact]
    public async Task ConfiguringAnAddressAfterAnUnconfiguredReadReachesTheInstance()
    {
        var instances = new RootsPerHost();
        var credentials = new RecordingCredentialPort();
        var port = PortOver(instances, credentials);

        Assert.Null(await port.ReadAsync(WhisparrGeneration.V3, TestCt));
        credentials.Holding(WhisparrGeneration.V3, FirstAddress, Key);

        Assert.Equal(["/media/first"], await port.ReadAsync(WhisparrGeneration.V3, TestCt));
    }

    // Held still, so what the two reads differ by is the address alone rather than an entry ageing
    // out between them.
    private static ReportedRootPort PortOver(
        RootsPerHost instances, RecordingCredentialPort credentials)
        => new(
            instances,
            credentials,
            new ReportedRootCache(new FixedClock(DateTimeOffset.UnixEpoch)),
            NullLogger.Instance);

    // Each instance declares a root naming itself, so an answer says which one produced it.
    private sealed class RootsPerHost : IWhisparrInstanceFactory
    {
        public List<WhisparrBinding> Bindings { get; } = [];

        public IWhisparrClient Bound(WhisparrBinding binding)
        {
            Bindings.Add(binding);

            var root = binding.BaseAddress.Host == "whisparr-first"
                ? "/media/first"
                : "/media/second";

            return new RecordingWhisparrV3Client(
                new WhisparrResponse(200, "application/json", $$"""[{"path":"{{root}}"}]"""));
        }
    }
}
