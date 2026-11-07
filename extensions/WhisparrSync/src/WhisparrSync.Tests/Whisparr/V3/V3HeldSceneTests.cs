using System.Net;
using System.Text.Json.Nodes;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// Asserted on the served answer rather than on source text, as the other outbound cases are. The
// identifiers are the uuid shape this generation names a scene by.
public sealed class V3HeldSceneTests
{
    private const string FirstScene = "1d468eaf-af0f-4f11-9dcf-9aa3cf62aa95";

    private const string SecondScene = "8b2c1e04-7a35-4d6f-9c81-52e0af3d7b16";

    private const string UnheldScene = "c40f9a72-6d18-4e2b-8f53-a91c7d604e38";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AHeldSceneTheInstanceHoldsNoFileForIsNamedAsRecordingNone()
    {
        var client = Over(Rows(Row(FirstScene, hasFile: true), Row(SecondScene, hasFile: false)));

        var held = await ((IWhisparrSceneStatusReading)client)
            .ReduceHeldScenesAsync([FirstScene, SecondScene], TestCt);

        Assert.Equal([FirstScene, SecondScene], held.Held.Order(StringComparer.Ordinal));
        Assert.Equal([SecondScene], held.WithNoFileRecorded);
    }

    // A row carrying no member for it is the instance claiming no file, not this product claiming
    // one on its behalf.
    [Fact]
    public async Task ARowCarryingNoFileMemberRecordsNoFile()
    {
        var client = Over(
            new JsonArray { new JsonObject { ["stashId"] = FirstScene } }.ToJsonString());

        var held = await ((IWhisparrSceneStatusReading)client)
            .ReduceHeldScenesAsync([FirstScene], TestCt);

        Assert.Equal([FirstScene], held.WithNoFileRecorded);
    }

    // A scene the instance holds no entry for is absent from both sets: naming it in the second
    // would count it once as not yet there and once as recording no file.
    [Fact]
    public async Task ASceneTheInstanceHoldsNoEntryForIsInNeitherSet()
    {
        var client = Over(Rows(Row(FirstScene, hasFile: false)));

        var held = await ((IWhisparrSceneStatusReading)client)
            .ReduceHeldScenesAsync([FirstScene, UnheldScene], TestCt);

        Assert.Equal([FirstScene], held.Held);
        Assert.Equal([FirstScene], held.WithNoFileRecorded);
    }

    [Fact]
    public async Task AnEmptyInputAnswersBothSetsEmptyWithNoRequest()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Rows());
        var client = TestWhisparrClient.Over(handler);

        var held = await ((IWhisparrSceneStatusReading)client).ReduceHeldScenesAsync([], TestCt);

        Assert.Empty(held.Held);
        Assert.Empty(held.WithNoFileRecorded);
        Assert.Empty(handler.Requests);
    }

    private static IWhisparrClient Over(string answered)
        => TestWhisparrClient.Over(BodyRecordingHandler.Answering(HttpStatusCode.OK, answered));

    private static string Rows(params JsonObject[] rows) => new JsonArray([.. rows]).ToJsonString();

    private static JsonObject Row(string foreignId, bool hasFile)
        => new()
        {
            ["id"] = 427,
            ["stashId"] = foreignId,
            ["foreignId"] = foreignId,
            ["hasFile"] = hasFile,
        };
}
