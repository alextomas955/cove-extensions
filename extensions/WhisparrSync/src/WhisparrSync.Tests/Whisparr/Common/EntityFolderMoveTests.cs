using System.Net;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// These cases read the serialized request rather than the arguments a seam was handed. Whether the
// instance moves terabytes is decided by what the request carries, and that is composed below the
// level a call site can see.
//
// One body per case, collected once per generation: both register the relocation role and each was
// driven across two real drives, so a claim made about one of them alone would be the gap that
// leaves an entity's registration behind on the other.
public sealed class EntityFolderMoveTests
{
    private const int EntityId = 9;

    private const string OldRoot = "/library/rootA";

    private const string AgreedRoot = "/library/rootB";

    private const string Folder = "Tushy";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // What each generation spells differently: the route its entity sits on, a member of its own
    // the composition never names, and the command that reads the new folder.
    private sealed record Relocating(
        string Route, string OwnMember, string OwnValue, string ReReadCommand, string IdsMember);

    private static Relocating Of(WhisparrGeneration generation)
        => generation is WhisparrGeneration.V3
            ? new Relocating("/movie/", "foreignId", "\"tushy-9\"", "RescanMovie", "movieIds")
            : new Relocating("/series/", "tvdbId", "3372", "RefreshSeries", "seriesId");

    private static string HeldAt(WhisparrGeneration generation, string path, string root)
        => $$"""
            {"id":{{EntityId}},"title":"{{Folder}}","{{Of(generation).OwnMember}}":{{Of(generation).OwnValue}},
             "path":"{{path}}","rootFolderPath":"{{root}}",
             "qualityProfileId":6,"monitored":true,"tags":[4,9],
             "seriesType":"standard","monitorNewItems":"none","seasons":[]}
            """;

    // The re-read is counted because the update alone records nothing readable: each generation
    // rewrites its own file record to the old file name under the new folder, which is a path
    // nothing holds until the folder is read again.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task OneReadOneUpdateAndTheFolderReReadLeave(WhisparrGeneration generation)
    {
        var (answered, sent) = await MoveAsync(generation);

        Assert.True(answered.StatusCode is >= 200 and < 300);
        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Put, HttpMethod.Post],
            sent.Requests.Select(request => request.Method));
        Assert.EndsWith(
            Of(generation).Route + EntityId, sent.Requests[0].Path, StringComparison.Ordinal);
        Assert.EndsWith(
            Of(generation).Route + EntityId, sent.Requests[1].Path, StringComparison.Ordinal);
        Assert.EndsWith("/command", sent.Requests[2].Path, StringComparison.Ordinal);
    }

    // The path is what relocates an entity. The root folder alone is accepted and relocates
    // nothing.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task TheUpdateCarriesThePathRecomposedUnderTheAgreedRoot(
        WhisparrGeneration generation)
    {
        var (_, sent) = await MoveAsync(generation);
        var body = UpdateBody(sent);

        Assert.Equal(AgreedRoot + "/" + Folder, body["path"]!.GetValue<string>());
        Assert.Equal(AgreedRoot, body["rootFolderPath"]!.GetValue<string>());
    }

    // The folder this product built is sent as it stands. Its last segment names the entity, and
    // the entity's own held path names wherever the instance put it, so carrying that segment
    // across would move the entity to a folder no entity owns.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task TheUpdateCarriesAFolderThisProductBuiltUnchanged(
        WhisparrGeneration generation)
    {
        var (_, sent) = await MoveAsync(generation, entityFolder: AgreedRoot + "/.tree/4628");

        Assert.Equal(AgreedRoot + "/.tree/4628", UpdateBody(sent)["path"]!.GetValue<string>());
    }

    // Every candidate the addressing port builds is forward-slashed whichever host the instance
    // runs on, so the agreed root cannot say which separator that host resolves. A Windows instance
    // does not resolve a path joined with the other one.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task TheUpdateIsSpelledWithTheSeparatorTheHeldPathUses(
        WhisparrGeneration generation)
    {
        var (_, sent) = await MoveAsync(
            generation,
            readAnswer: HeldAt(generation, @"D:\\MediaA\\" + Folder, @"D:\\MediaA"),
            agreedRoot: "D:/MediaB");
        var body = UpdateBody(sent);

        Assert.Equal(@"D:\MediaB\" + Folder, body["path"]!.GetValue<string>());
        Assert.Equal(@"D:\MediaB", body["rootFolderPath"]!.GetValue<string>());
    }

    // Absence is the guarantee, measured against a live instance of each generation holding linked
    // files under the old root: with the transfer parameter absent the bytes stay where they are. A
    // request naming it at all could move a library.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task TheUpdateInstructsNoFileTransfer(WhisparrGeneration generation)
    {
        var (_, sent) = await MoveAsync(generation);

        Assert.DoesNotContain("moveFiles", sent.Targets[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "moveFiles", sent.Requests[1].Body, StringComparison.OrdinalIgnoreCase);
    }

    // The update is a re-send rather than a replacement. Composing a member set here would drop the
    // instance's own tags, profile and per-year flags with nothing saying so.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task AMemberTheCompositionDoesNotNameSurvivesIntoTheUpdate(
        WhisparrGeneration generation)
    {
        var (_, sent) = await MoveAsync(generation);
        var body = UpdateBody(sent);

        Assert.Equal([4, 9], ((JsonArray)body["tags"]!).Select(tag => tag!.GetValue<int>()));
        Assert.Equal(6, body["qualityProfileId"]!.GetValue<int>());
        Assert.True(body["monitored"]!.GetValue<bool>());
        Assert.Equal(Of(generation).OwnValue, body[Of(generation).OwnMember]!.ToJsonString());
    }

    // There is nothing to re-send. An update composed from a refused read would write a member set
    // this product named over an entity it never saw.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task AReadTheInstanceRefusedProducesNoUpdate(WhisparrGeneration generation)
    {
        var (answered, sent) = await MoveAsync(generation, readStatus: HttpStatusCode.NotFound);

        Assert.Equal((int)HttpStatusCode.NotFound, answered.StatusCode);
        Assert.Equal([HttpMethod.Get], sent.Requests.Select(request => request.Method));
    }

    // The status alone is a success here, so an answer handed back unchanged would count as a move
    // that happened while the entity is still registered where none of its files sit.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task AReadAnsweringNothingReadableSendsNoUpdateAndIsNotAccepted(
        WhisparrGeneration generation)
    {
        var (answered, sent) = await MoveAsync(generation, readAnswer: "null");

        Assert.Equal([HttpMethod.Get], sent.Requests.Select(request => request.Method));
        Assert.NotEqual(MonitorRefusalKind.None, MonitoringProjector.Accepted(answered));
    }

    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task AnUpdateTheInstanceRefusedStopsBeforeTheFolderReRead(
        WhisparrGeneration generation)
    {
        var (answered, sent) = await MoveAsync(
            generation, updateStatus: HttpStatusCode.BadRequest);

        Assert.Equal((int)HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Put], sent.Requests.Select(request => request.Method));
    }

    // The command each generation reads a folder with, named with that generation's own id member.
    // A body carrying the other's shape is accepted and runs over nothing.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task TheReReadNamesTheFolderCommandAndTheMovedEntity(
        WhisparrGeneration generation)
    {
        var (_, sent) = await MoveAsync(generation);
        var command = Assert.IsType<JsonObject>(JsonNode.Parse(sent.Requests[2].Body));
        var named = command[Of(generation).IdsMember]!;

        Assert.Equal(Of(generation).ReReadCommand, command["name"]!.GetValue<string>());
        Assert.Equal(
            [EntityId],
            named is JsonArray ids
                ? ids.Select(id => id!.GetValue<int>())
                : [named.GetValue<int>()]);
    }

    private static JsonObject UpdateBody(BodyRecordingHandler sent)
        => Assert.IsType<JsonObject>(JsonNode.Parse(sent.Requests[1].Body));

    private static async Task<(WhisparrResponse Answered, BodyRecordingHandler Sent)> MoveAsync(
        WhisparrGeneration generation,
        HttpStatusCode readStatus = HttpStatusCode.OK,
        HttpStatusCode updateStatus = HttpStatusCode.Accepted,
        string? readAnswer = null,
        string agreedRoot = AgreedRoot,
        string? entityFolder = null)
    {
        var held = readAnswer ?? HeldAt(generation, OldRoot + "/" + Folder, OldRoot);
        var sent = BodyRecordingHandler.AnsweringEach((method, _) => Answer(method));

        (HttpStatusCode Status, string Answer) Answer(HttpMethod method)
        {
            if (method == HttpMethod.Get)
            {
                return (readStatus, held);
            }

            return method == HttpMethod.Put
                ? (updateStatus, held)
                : (HttpStatusCode.Created, $$"""{"id":41,"name":"{{Of(generation).ReReadCommand}}"}""");
        }

        var client = (IWhisparrEntityRelocationActing)TestWhisparrClient.Over(
            sent, generation: generation);
        var answered = await client.MoveEntityFolderAsync(
            EntityId, agreedRoot, entityFolder, TestCt);

        return (answered, sent);
    }
}
