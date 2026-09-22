using System.Net;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// These cases read the serialized request rather than the arguments a seam was handed. Whether
// the instance moves terabytes is decided by what the request carries, and that is composed below
// the level a call site can see.
public sealed class SiteRootMoveTests
{
    private const int SiteId = 9;

    private const string OldRoot = "/library/rootA";

    private const string AgreedRoot = "/library/rootB";

    private const string Folder = "Tushy";

    // The site as the instance answers it, carrying members this product never names.
    private static readonly string Held = $$"""
        {"id":{{SiteId}},"title":"{{Folder}}","tvdbId":3372,
         "path":"{{OldRoot}}/{{Folder}}","rootFolderPath":"{{OldRoot}}",
         "qualityProfileId":6,"monitored":true,"tags":[4,9],
         "seriesType":"standard","monitorNewItems":"none","seasons":[]}
        """;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The catalogue re-read is counted because the update alone links nothing: a site moved onto
    // the root its media sits under still reports no file until the catalogue is read again.
    [Fact]
    public async Task OneReadOneUpdateAndTheCatalogueReReadLeave()
    {
        var (answered, sent) = await MoveAsync();

        Assert.True(answered.StatusCode is >= 200 and < 300);
        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Put, HttpMethod.Post],
            sent.Requests.Select(request => request.Method));
        Assert.EndsWith(
            "/series/" + SiteId, sent.Requests[0].Path, StringComparison.Ordinal);
        Assert.EndsWith(
            "/series/" + SiteId, sent.Requests[1].Path, StringComparison.Ordinal);
        Assert.EndsWith("/command", sent.Requests[2].Path, StringComparison.Ordinal);
    }

    // The path is what relocates a site. The root folder alone is accepted and relocates nothing.
    [Fact]
    public async Task TheUpdateCarriesThePathRecomposedUnderTheAgreedRoot()
    {
        var (_, sent) = await MoveAsync();
        var body = UpdateBody(sent);

        Assert.Equal(AgreedRoot + "/" + Folder, body["path"]!.GetValue<string>());
        Assert.Equal(AgreedRoot, body["rootFolderPath"]!.GetValue<string>());
    }

    // Every candidate the addressing port builds is forward-slashed whichever host the instance
    // runs on, so the agreed root cannot say which separator that host resolves. A Windows instance
    // does not resolve a path joined with the other one.
    [Fact]
    public async Task TheUpdateIsSpelledWithTheSeparatorTheHeldPathUses()
    {
        var (_, sent) = await MoveAsync(
            readAnswer: $$"""
                {"id":{{SiteId}},"title":"{{Folder}}",
                 "path":"D:\\MediaA\\{{Folder}}","rootFolderPath":"D:\\MediaA",
                 "qualityProfileId":6,"monitored":true,"seasons":[]}
                """,
            agreedRoot: "D:/MediaB");
        var body = UpdateBody(sent);

        Assert.Equal(@"D:\MediaB\" + Folder, body["path"]!.GetValue<string>());
        Assert.Equal(@"D:\MediaB", body["rootFolderPath"]!.GetValue<string>());
    }

    // Absence is the guarantee, measured against a live instance: with the transfer parameter
    // absent the bytes stay under the old root. A request naming it at all could move a library.
    [Fact]
    public async Task TheUpdateInstructsNoFileTransfer()
    {
        var (_, sent) = await MoveAsync();

        Assert.DoesNotContain(
            "moveFiles", sent.Targets[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "moveFiles", sent.Requests[1].Body, StringComparison.OrdinalIgnoreCase);
    }

    // The update is a re-send rather than a replacement. Composing a member set here would drop
    // the instance's own tags, profile and per-year flags with nothing saying so.
    [Fact]
    public async Task AMemberTheCompositionDoesNotNameSurvivesIntoTheUpdate()
    {
        var (_, sent) = await MoveAsync();
        var body = UpdateBody(sent);

        Assert.Equal([4, 9], ((JsonArray)body["tags"]!).Select(tag => tag!.GetValue<int>()));
        Assert.Equal(6, body["qualityProfileId"]!.GetValue<int>());
        Assert.True(body["monitored"]!.GetValue<bool>());
        Assert.Equal(3372, body["tvdbId"]!.GetValue<int>());
    }

    // There is nothing to re-send. An update composed from a refused read would write a member set
    // this product named over a site it never saw.
    [Fact]
    public async Task AReadTheInstanceRefusedProducesNoUpdate()
    {
        var (answered, sent) = await MoveAsync(readStatus: HttpStatusCode.NotFound);

        Assert.Equal((int)HttpStatusCode.NotFound, answered.StatusCode);
        Assert.Equal([HttpMethod.Get], sent.Requests.Select(request => request.Method));
    }

    // The status alone is a success here, so an answer handed back unchanged would count as a move
    // that happened while the site is still registered where none of its files sit.
    [Fact]
    public async Task AReadAnsweringNothingReadableSendsNoUpdateAndIsNotAccepted()
    {
        var (answered, sent) = await MoveAsync(readAnswer: "null");

        Assert.Equal([HttpMethod.Get], sent.Requests.Select(request => request.Method));
        Assert.NotEqual(MonitorRefusalKind.None, MonitoringProjector.Accepted(answered));
    }

    [Fact]
    public async Task AnUpdateTheInstanceRefusedStopsBeforeTheCatalogueReRead()
    {
        var (answered, sent) = await MoveAsync(updateStatus: HttpStatusCode.BadRequest);

        Assert.Equal((int)HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Put], sent.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task TheReReadNamesTheCatalogueCommandAndTheMovedSite()
    {
        var (_, sent) = await MoveAsync();
        var command = Assert.IsType<JsonObject>(JsonNode.Parse(sent.Requests[2].Body));

        Assert.Equal(
            V2BodyProjector.RefreshSeriesCommand, command["name"]!.GetValue<string>());
        Assert.Equal(SiteId, command["seriesId"]!.GetValue<int>());
    }

    private static JsonObject UpdateBody(BodyRecordingHandler sent)
        => Assert.IsType<JsonObject>(JsonNode.Parse(sent.Requests[1].Body));

    private static async Task<(WhisparrResponse Answered, BodyRecordingHandler Sent)> MoveAsync(
        HttpStatusCode readStatus = HttpStatusCode.OK,
        HttpStatusCode updateStatus = HttpStatusCode.Accepted,
        string? readAnswer = null,
        string agreedRoot = AgreedRoot)
    {
        var sent = BodyRecordingHandler.AnsweringEach((method, _) => Answer(method));

        (HttpStatusCode Status, string Answer) Answer(HttpMethod method)
        {
            if (method == HttpMethod.Get)
            {
                return (readStatus, readAnswer ?? Held);
            }

            return method == HttpMethod.Put
                ? (updateStatus, Held)
                : (HttpStatusCode.Created, """{"id":41,"name":"RefreshSeries"}""");
        }

        var client = (IWhisparrSiteRegistrationActing)TestWhisparrClient.Over(
            sent, generation: WhisparrGeneration.V2);
        var answered = await client.MoveSiteRootAsync(SiteId, agreedRoot, TestCt);

        return (answered, sent);
    }
}
