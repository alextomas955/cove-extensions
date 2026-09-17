using System.Net;
using System.Text.Json.Nodes;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// What moving a held site's root actually SENDS, read off the serialized request.
/// </summary>
/// <remarks>
/// The transfer guarantee is the reason this file asserts bytes rather than arguments. Whether the
/// instance moves terabytes is decided by what the request carries, which is composed below the
/// level a call site can see, so comparing a value a seam was handed against itself would be no
/// evidence at all. The same rule <c>ReflectOwnedNeverTransfersTests</c> states for the import path.
/// <para>
/// The instance transfers nothing when the transfer parameter is absent, measured against a live
/// instance whose files sat under the old root. So the guarantee here is the parameter's absence,
/// and an edit that begins naming it is what these cases go red on.
/// </para>
/// </remarks>
public sealed class SiteRootMoveTests
{
    private static readonly Uri Instance = new("http://whisparr-v2:6969/");

    private const string ApiKey = "7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c";

    /// <summary>The instance's own numeric id for the site, already resolved upstream.</summary>
    private const int SiteId = 9;

    private const string OldRoot = "/library/rootA";

    private const string AgreedRoot = "/library/rootB";

    private const string Folder = "Tushy";

    /// <summary>
    /// The site as the instance answers it, carrying members this product never names.
    /// </summary>
    private static readonly string Held = $$"""
        {"id":{{SiteId}},"title":"{{Folder}}","tvdbId":3372,
         "path":"{{OldRoot}}/{{Folder}}","rootFolderPath":"{{OldRoot}}",
         "qualityProfileId":6,"monitored":true,"tags":[4,9],
         "seriesType":"standard","monitorNewItems":"none","seasons":[]}
        """;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>One read, one update and the catalogue re-read, and nothing else.</summary>
    /// <remarks>
    /// The re-read is counted here because the update alone links nothing: a site moved onto the root
    /// its media really sits under still reports no file until the catalogue is read again.
    /// </remarks>
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

    /// <summary>
    /// The update's path is the agreed root and the site's own existing last segment.
    /// </summary>
    /// <remarks>
    /// The path is what relocates a site; the root folder alone is accepted and relocates nothing.
    /// </remarks>
    [Fact]
    public async Task TheUpdateCarriesThePathRecomposedUnderTheAgreedRoot()
    {
        var (_, sent) = await MoveAsync();
        var body = UpdateBody(sent);

        Assert.Equal(AgreedRoot + "/" + Folder, body["path"]!.GetValue<string>());
        Assert.Equal(AgreedRoot, body["rootFolderPath"]!.GetValue<string>());
    }

    /// <summary>The update instructs no file transfer, in the request or in the body.</summary>
    /// <remarks>
    /// Absence is the guarantee. The instance leaves the bytes under the old root when the parameter
    /// is absent, so the request naming it at all is the change that could move a library.
    /// </remarks>
    [Fact]
    public async Task TheUpdateInstructsNoFileTransfer()
    {
        var (_, sent) = await MoveAsync();

        Assert.DoesNotContain(
            "moveFiles", sent.Targets[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "moveFiles", sent.Requests[1].Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A member the composition does not name survives from the read into the update.
    /// </summary>
    /// <remarks>
    /// Which is what makes the update a re-send rather than a replacement. Composing a member set
    /// here would drop the instance's own tags, profile and per-year flags with nothing saying so.
    /// </remarks>
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

    /// <summary>A read the instance refused produces no update at all.</summary>
    /// <remarks>
    /// There is nothing to re-send. An update composed from an answer nothing could be read out of
    /// would write a member set this product named over a site it never saw.
    /// </remarks>
    [Fact]
    public async Task AReadTheInstanceRefusedProducesNoUpdate()
    {
        var (answered, sent) = await MoveAsync(readStatus: HttpStatusCode.NotFound);

        Assert.Equal((int)HttpStatusCode.NotFound, answered.StatusCode);
        Assert.Equal([HttpMethod.Get], sent.Requests.Select(request => request.Method));
    }

    /// <summary>An update the instance refused stops before the catalogue re-read.</summary>
    [Fact]
    public async Task AnUpdateTheInstanceRefusedStopsBeforeTheCatalogueReRead()
    {
        var (answered, sent) = await MoveAsync(updateStatus: HttpStatusCode.BadRequest);

        Assert.Equal((int)HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Put], sent.Requests.Select(request => request.Method));
    }

    /// <summary>The re-read names this generation's own catalogue command and the moved site.</summary>
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

    /// <summary>One move against an instance holding the site under the other root.</summary>
    private static async Task<(WhisparrResponse Answered, BodyRecordingHandler Sent)> MoveAsync(
        HttpStatusCode readStatus = HttpStatusCode.OK,
        HttpStatusCode updateStatus = HttpStatusCode.Accepted)
    {
        var sent = BodyRecordingHandler.AnsweringEach((method, _) => Answer(method));

        (HttpStatusCode Status, string Answer) Answer(HttpMethod method)
        {
            if (method == HttpMethod.Get)
            {
                return (readStatus, Held);
            }

            return method == HttpMethod.Put
                ? (updateStatus, Held)
                : (HttpStatusCode.Created, """{"id":41,"name":"RefreshSeries"}""");
        }

        var client = TestWhisparrClient.Over(sent);
        var answered = await client.MoveSiteRootAsync(Instance, ApiKey, SiteId, AgreedRoot, TestCt);

        return (answered, sent);
    }
}
