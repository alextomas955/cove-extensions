using System.Net;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// Driven over a byte-level stub, so the routes the page reaches are what these cases assert. This
// generation's site list recomputes statistics over every site before it answers, so a page of
// cards is asked for one site at a time and the list is never read here.
public sealed class V2SiteCardBatchTests
{
    private const string StudioUuid = "e3b61b3e-0c20-4bea-9441-b88430ed6317";
    private const string OtherUuid = "5ee16943-0da6-4ee4-94c1-54172e3d0b7e";
    private const string SomeKey = "0123456789abcdef0123456789abcdef";

    private static readonly Uri SomeAddress = new("http://whisparr:6969");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static string Site(int rowId, bool monitored) =>
        $$"""[{"id":{{rowId}},"tvdbId":92,"title":"Some Site","monitored":{{(monitored ? "true" : "false")}}}]""";

    [Fact]
    public async Task APageOfCardsAsksTheLookupOncePerCardAndNeverReadsTheSiteList()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Site(7, monitored: false));
        using var http = new HttpClient(handler);

        await ReadAsync(http, handler, [StudioUuid, OtherUuid]);

        Assert.Equal(2, handler.Targets.Count);
        Assert.All(
            handler.Targets,
            target => Assert.StartsWith("/api/v3/series/lookup", target, StringComparison.Ordinal));
        Assert.DoesNotContain("/api/v3/series?", string.Join(' ', handler.Targets), StringComparison.Ordinal);
    }

    // A site the instance holds is answered from its own row, so the flag is the stored one rather
    // than the metadata source's default.
    [Fact]
    public async Task ASiteTheInstanceHoldsCarriesTheMonitoredFlagItStored()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Site(7, monitored: true));
        using var http = new HttpClient(handler);

        var cards = await ReadAsync(http, handler, [StudioUuid]);

        Assert.Equal(new WhisparrHeldCard(true, null), cards.Held[StudioUuid]);
        Assert.Empty(cards.NotAnswered);
    }

    // A row carrying no id of the instance's own was mapped from the metadata source, which is the
    // instance answering that it holds no site under that identifier.
    [Fact]
    public async Task ASiteTheInstanceDoesNotHoldIsHeldByNothingRatherThanUnanswered()
    {
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, """[{"tvdbId":92,"title":"Some Site"}]""");
        using var http = new HttpClient(handler);

        var cards = await ReadAsync(http, handler, [StudioUuid]);

        Assert.Empty(cards.Held);
        Assert.Empty(cards.NotAnswered);
    }

    // Recorded from the pinned build for a site it holds nothing under. The metadata row it maps
    // instead carries monitored true, so a reading taken without the instance's own id would draw
    // every unheld site as one the instance is monitoring.
    [Fact]
    public async Task AMonitoredFlagOnARowTheInstanceHoldsNothingUnderIsNotRead()
    {
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK,
            ProbeFixtures.Read("whisparr-v2-2.2.0.231-series-lookup-not-held.json"));
        using var http = new HttpClient(handler);

        var cards = await ReadAsync(http, handler, [StudioUuid]);

        Assert.Empty(cards.Held);
        Assert.Empty(cards.NotAnswered);
    }

    // Reported apart from an absence: an absence states the instance holds no such site, which no
    // instance said here.
    [Fact]
    public async Task AnAnswerThatIsNotAListOfSitesLeavesTheCardUnanswered()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, """{"message":"no"}""");
        using var http = new HttpClient(handler);

        var cards = await ReadAsync(http, handler, [StudioUuid]);

        Assert.Empty(cards.Held);
        Assert.Equal([StudioUuid], cards.NotAnswered);
    }

    // The number the metadata source issued is asked for under the prefix the lookup answers from
    // its own rows, so a library already holding the number costs no outbound search.
    [Fact]
    public async Task AnIdentifierThatIsAlreadyANumberIsAskedForAsThatSourcesOwnId()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Site(7, monitored: false));
        using var http = new HttpClient(handler);

        await ReadAsync(http, handler, ["3372"]);

        Assert.Contains("tpdb%3a3372", Assert.Single(handler.Targets), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AUuidIsAskedForAsTheSearchTermItIs()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Site(7, monitored: false));
        using var http = new HttpClient(handler);

        await ReadAsync(http, handler, [StudioUuid]);

        Assert.Contains(StudioUuid, Assert.Single(handler.Targets), StringComparison.Ordinal);
        Assert.DoesNotContain("tpdb", Assert.Single(handler.Targets), StringComparison.Ordinal);
    }

    private static Task<WhisparrHeldCards> ReadAsync(
        HttpClient http, BodyRecordingHandler handler, string[] foreignIds)
        => TestWhisparrClient.Over(http, handler).ReadHeldEntitiesAsync(
            SomeAddress,
            SomeKey,
            WhisparrGeneration.V2,
            WhisparrEntityKind.Studio,
            foreignIds,
            TestCt);
}
