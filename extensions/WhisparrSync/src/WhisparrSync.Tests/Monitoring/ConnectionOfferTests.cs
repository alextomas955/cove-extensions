using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// The selection bar's menu describes the connection, so this route answers from stored settings and
// reaches the instance for nothing. A read that did would make the menu as slow as whatever the
// instance is busy with, for facts the menu already holds.
public sealed class ConnectionOfferTests
{
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheOfferIsAnsweredWithoutReachingTheInstance()
    {
        await using var host = await MonitorHost.CreateAsync(
            bytes: BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]"));

        var offer = await ReadAsync(host);

        Assert.True(offer.Configured);
        Assert.Empty(host.Bytes!.Requests);
    }

    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task TheOfferNamesTheStoredGenerationAndWhatItCanDo(WhisparrGeneration generation)
    {
        await using var host = await MonitorHost.CreateAsync(generation: generation);

        var offer = await ReadAsync(host);

        Assert.Equal(generation, offer.Generation);
        Assert.Equal(
            GenerationCapabilities.CapabilitiesOf(generation).Order(),
            offer.Capabilities.Order());
    }

    // Nothing stored is a fact about the connection, so the menu reads it here rather than from a
    // read of an entity that would have refused for the same reason.
    [Fact]
    public async Task NothingStoredIsAnsweredAsAConnectionThatCanBeAskedForNothing()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);

        var offer = await ReadAsync(host);

        Assert.False(offer.Configured);
        Assert.Null(offer.Generation);
        Assert.Empty(offer.Capabilities);
    }

    [Fact]
    public async Task ACallerHoldingNothingIsRefused()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions());

        var answered = await host.Http.GetAsync(host.ConnectionOfferRoute, TestCt);

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
    }

    // The read tier, the one a library viewer already holds: the route states what the connection
    // can do and changes nothing.
    [Fact]
    public async Task AReadingCallerIsServed()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        var answered = await host.Http.GetAsync(host.ConnectionOfferRoute, TestCt);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
    }

    private static async Task<WhisparrConnectionOffer> ReadAsync(MonitorHost host)
    {
        var answered = await host.Http.GetAsync(host.ConnectionOfferRoute, TestCt);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<WhisparrConnectionOffer>(TestCt))!;
    }
}
