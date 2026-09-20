using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

public sealed class MissingSceneActionTests
{
    private static readonly CancellationToken TestCt = TestContext.Current.CancellationToken;

    private const string SceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static string MonitorVerb(string providerSceneId)
        => $"missing/{providerSceneId}/monitor";

    private static string SearchVerb(string providerSceneId)
        => $"missing/{providerSceneId}/search";

    private const int SceneOnTheInstance = 812;

    private const string PostedCommandRow =
        """{"id":9001,"name":"MoviesSearch","status":"queued"}""";

    private static string HeldSceneRow(bool monitored)
        => $$"""[{"id":{{SceneOnTheInstance}},"monitored":{{(monitored ? "true" : "false")}}}]""";

    private static Task<int> StudioIn(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

    private static async Task<MissingSceneActionResult> ReadResultAsync(HttpResponseMessage answered)
    {
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<MissingSceneActionResult>(TestCt))!;
    }

    [Fact]
    public async Task TheMonitorVerbRegistersTheSceneAndIssuesNoGrabbingRequest()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneAsync), MonitorHost.Json(200, "{}"));
        var studioId = await StudioIn(host);

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, MonitorVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.None, result.Refusal);
        Assert.Equal(MissingSceneState.Monitored, result.State);

        Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync));
        Assert.All(
            host.Client.Verbs,
            sent => Assert.NotEqual(
                WhisparrVerbClass.Grab, Invariants.OutboundSeam.VerbClassByMember[sent]));
    }

    [Fact]
    public async Task TheBodyTheInstanceReceivesAcquiresNothing()
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, MonitorHost.UnsortedProfiles),
            (HttpStatusCode.OK, MonitorHost.OneRootFolder),
            (HttpStatusCode.Created, """{"id":7,"monitored":true}"""));
        await using var host = await MonitorHost.CreateAsync(bytes: handler);
        var studioId = await StudioIn(host);

        await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, MonitorVerb(SceneId), "{}"));

        var add = Assert.Single(handler.Requests, sent => sent.Method == HttpMethod.Post);
        var body = Assert.IsType<JsonObject>(JsonNode.Parse(add.Body));

        Assert.Equal(SceneId, body["foreignId"]!.GetValue<string>());
        Assert.True(body["monitored"]!.GetValue<bool>());

        var suppression = ComposedAdds.At(body, ComposedAdds.SceneSuppression);
        Assert.NotNull(suppression);
        Assert.False(suppression.GetValue<bool>());

        Assert.DoesNotContain(
            ComposedAdds.GrabbingCommandNames,
            name => add.Body.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain("/command", add.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSceneIdentifierArrivesFromTheRoutePathAndNotFromABody()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneAsync), MonitorHost.Json(200, "{}"));
        var studioId = await StudioIn(host);

        await ReadResultAsync(
            await host.PostRawAsync(
                "studio",
                studioId,
                MonitorVerb(SceneId),
                """{"providerSceneId":"a-scene-the-caller-named-in-a-body"}"""));

        var added = Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync));
        Assert.Equal(SceneId, added.ForeignId);
    }

    [Theory]
    [InlineData("%20")]
    [InlineData("%20leading-space")]
    [InlineData("trailing-space%20")]
    public async Task AnIdentifierOutsideTheBoundReachesNoOutboundRequest(string providerSceneId)
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);

        var answered = await host.PostRawAsync(
            "studio", studioId, MonitorVerb(providerSceneId), "{}");

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.DoesNotContain(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync));
    }

    [Fact]
    public async Task AnIdentifierLongerThanTheBoundReachesNoOutboundRequest()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);

        var answered = await host.PostRawAsync(
            "studio", studioId, MonitorVerb(new string('a', 129)), "{}");

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.DoesNotContain(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync));
    }

    [Fact]
    public async Task AVerbWithNothingConnectedReadsAsHavingReachedWhisparrNotAtAll()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);
        var studioId = await StudioIn(host);

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, MonitorVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.DidNotReachWhisparr, result.Refusal);
        Assert.Equal(MissingSceneState.StatusUnknown, result.State);
        Assert.Empty(host.Client.Acting);
    }

    [Fact]
    public async Task AnAnswerThatStopsPartWayReadsAsHavingReachedWhisparrNotAtAll()
    {
        await using var host = await MonitorHost.CreateAsync(
            bytes: BodyRecordingHandler.AnsweringWithABodyThatStopsPartWay());
        var studioId = await StudioIn(host);

        var answered = await host.PostRawAsync("studio", studioId, MonitorVerb(SceneId), "{}");

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal(
            MissingSceneActionRefusal.DidNotReachWhisparr, (await ReadResultAsync(answered)).Refusal);
    }

    [Fact]
    public async Task AnAnswerThatDeclinedReadsAsTheInstanceRefusingIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);
        host.Client.Answering(
            nameof(IWhisparrMissingSceneActing.AddSceneAsync),
            MonitorHost.Json(409, """{"message":"already exists"}"""));

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, MonitorVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.InstanceRefused, result.Refusal);
        Assert.Equal(MissingSceneState.StatusUnknown, result.State);
    }

    [Fact]
    public async Task AnInstanceOfferingNoQualityProfileIsNamedAsTheCause()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);
        host.Client.Answering(
            nameof(IWhisparrClient.ReadQualityProfilesAsync), MonitorHost.Json(200, "[]"));

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, MonitorVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.InstanceOffersNoQualityProfile, result.Refusal);
        Assert.DoesNotContain(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync));
    }

    [Fact]
    public async Task AnInstanceOfferingNoRootFolderIsNamedAsTheCause()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);
        host.Client.Answering(
            nameof(IWhisparrClient.ReadRootFoldersAsync), MonitorHost.Json(200, "[]"));

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, MonitorVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.InstanceOffersNoRootFolder, result.Refusal);
        Assert.DoesNotContain(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync));
    }

    // Measured against Whisparr 3.4.0.1387: MoviesSearch answers 201 and echoes its movieIds
    // member, while SceneSearch, MovieSearch and an unregistered name each answer 400 "Unknown
    // command type". A wrong id member is accepted and dropped, so the command then runs over
    // nothing.
    [Fact]
    public async Task TheSearchSendsOneCommandNamingTheSceneTheInstanceHolds()
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, HeldSceneRow(monitored: true)),
            (HttpStatusCode.Created, PostedCommandRow),
            (HttpStatusCode.OK, PostedCommandRow));
        await using var host = await MonitorHost.CreateAsync(bytes: handler);
        var studioId = await StudioIn(host);

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, SearchVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.None, result.Refusal);

        var command = Assert.Single(handler.Requests, sent => sent.Method == HttpMethod.Post);
        var body = Assert.IsType<JsonObject>(JsonNode.Parse(command.Body));

        Assert.Contains("/command", command.Path, StringComparison.Ordinal);
        Assert.Contains(V3BodyProjector.ScenesSearchCommand, command.Body, StringComparison.Ordinal);
        Assert.Equal(
            [SceneOnTheInstance],
            body[V3BodyProjector.SceneIdsProperty]!.AsArray().Select(id => id!.GetValue<int>()));
        Assert.DoesNotContain(SceneId, command.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSceneIsReadBeforeTheCommandAndTheCommandIsReadAfterIt()
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, HeldSceneRow(monitored: true)),
            (HttpStatusCode.Created, PostedCommandRow),
            (HttpStatusCode.OK, PostedCommandRow));
        await using var host = await MonitorHost.CreateAsync(bytes: handler);
        var studioId = await StudioIn(host);

        await ReadResultAsync(await host.PostRawAsync("studio", studioId, SearchVerb(SceneId), "{}"));

        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Post, HttpMethod.Get],
            handler.Requests.Select(sent => sent.Method));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "[]")]
    [InlineData(HttpStatusCode.NotFound, "")]
    public async Task ASceneTheInstanceHoldsNoEntryForIsStatedAndReachesNoCommand(
        HttpStatusCode status, string answer)
    {
        var handler = BodyRecordingHandler.AnsweringInTurn((status, answer));
        await using var host = await MonitorHost.CreateAsync(bytes: handler);
        var studioId = await StudioIn(host);

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, SearchVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.WhisparrHasNoEntryForScene, result.Refusal);
        Assert.Equal(MissingSceneState.NotAdded, result.State);
        Assert.DoesNotContain(handler.Requests, sent => sent.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(true, MissingSceneState.Monitored)]
    [InlineData(false, MissingSceneState.Unmonitored)]
    public async Task TheAnswerCarriesTheStateReadOffTheSceneRow(
        bool monitored, MissingSceneState expected)
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, HeldSceneRow(monitored)),
            (HttpStatusCode.Created, PostedCommandRow),
            (HttpStatusCode.OK, PostedCommandRow));
        await using var host = await MonitorHost.CreateAsync(bytes: handler);
        var studioId = await StudioIn(host);

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, SearchVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.None, result.Refusal);
        Assert.Equal(expected, result.State);
    }

    [Fact]
    public async Task ARowCarryingNoUsableIdentifierReachesNoCommand()
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, """[{"monitored":true}]"""));
        await using var host = await MonitorHost.CreateAsync(bytes: handler);
        var studioId = await StudioIn(host);

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, SearchVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.InstanceRefused, result.Refusal);
        Assert.DoesNotContain(handler.Requests, sent => sent.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ASearchOverAnUnreadableSceneReadReachesNoCommand()
    {
        var handler = BodyRecordingHandler.AnsweringInTurn((HttpStatusCode.InternalServerError, ""));
        await using var host = await MonitorHost.CreateAsync(bytes: handler);
        var studioId = await StudioIn(host);

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, SearchVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.DidNotReachWhisparr, result.Refusal);
        Assert.DoesNotContain(handler.Requests, sent => sent.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ACommandTheInstanceDeclinedReadsAsTheInstanceRefusingIt()
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, HeldSceneRow(monitored: true)),
            (HttpStatusCode.BadRequest, """{"message":"Unknown command type"}"""));
        await using var host = await MonitorHost.CreateAsync(bytes: handler);
        var studioId = await StudioIn(host);

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, SearchVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.InstanceRefused, result.Refusal);
        Assert.Equal(MissingSceneState.StatusUnknown, result.State);
    }

    [Fact]
    public async Task ASearchWithNothingConnectedReadsAsHavingReachedWhisparrNotAtAll()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);
        var studioId = await StudioIn(host);

        var result = await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, SearchVerb(SceneId), "{}"));

        Assert.Equal(MissingSceneActionRefusal.DidNotReachWhisparr, result.Refusal);
        Assert.Empty(host.Client.Acting);
    }

    [Theory]
    [InlineData("monitor")]
    [InlineData("search")]
    public async Task AGenerationHoldingNoRoleForTheVerbNamesTheAbsentRole(string verb)
    {
        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var studioId = await StudioIn(host);

        var result = await ReadResultAsync(
            await host.PostRawAsync(
                "studio",
                studioId,
                verb == "monitor" ? MonitorVerb(SceneId) : SearchVerb(SceneId),
                "{}"));

        Assert.Equal(
            MissingSceneActionRefusal.CapabilityAbsentOnThisGeneration, result.Refusal);
        Assert.NotEqual(MissingSceneActionRefusal.InstanceRefused, result.Refusal);
        Assert.Empty(host.Client.Acting);
    }

    [Fact]
    public async Task TheSearchVerbIsClassedGrabbingAndTheMonitorVerbIsNot()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.SearchSceneAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneAsync), MonitorHost.Json(200, "{}"));
        var studioId = await StudioIn(host);
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(200, HeldSceneRow(monitored: true)));

        await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, MonitorVerb(SceneId), "{}"));
        var monitorVerbs = host.Client.Verbs.ToList();

        await ReadResultAsync(
            await host.PostRawAsync("studio", studioId, SearchVerb(SceneId), "{}"));

        Assert.NotEmpty(monitorVerbs);
        Assert.All(
            monitorVerbs,
            sent => Assert.NotEqual(
                WhisparrVerbClass.Grab, Invariants.OutboundSeam.VerbClassByMember[sent]));
        Assert.Contains(
            host.Client.Verbs.Skip(monitorVerbs.Count),
            sent => Invariants.OutboundSeam.VerbClassByMember[sent] == WhisparrVerbClass.Grab);
    }

    [Theory]
    [InlineData("monitor")]
    [InlineData("search")]
    public async Task NeitherPerSceneVerbIsReachableBelowTheConfigureTier(string verb)
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));
        var studioId = await StudioIn(host);

        var answered = await host.PostRawAsync(
            "studio", studioId, $"missing/{SceneId}/{verb}", "{}");

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
        Assert.Empty(host.Client.Acting);
    }
}
