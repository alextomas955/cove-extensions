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

/// <summary>What one card's own verb sends, and what each answer it can get reads back as.</summary>
/// <remarks>
/// The card's refusal vocabulary is separate from the grid's, and the sentence a reader sees is
/// chosen in the browser from the value answered here, so every value has to be reachable from an
/// answer an instance can really give. A value nothing reaches leaves a branch of the card's failure
/// line unreachable and untested.
/// </remarks>
public sealed class MissingSceneActionTests
{
    private static readonly CancellationToken TestCt = TestContext.Current.CancellationToken;

    /// <summary>A catalogue scene as the provider issues its identifier.</summary>
    private const string SceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static string MonitorVerb(string providerSceneId)
        => $"missing/{providerSceneId}/monitor";

    private static string SearchVerb(string providerSceneId)
        => $"missing/{providerSceneId}/search";

    /// <summary>The instance's own identifier for the scene, as its row carries one.</summary>
    private const int SceneOnTheInstance = 812;

    /// <summary>The command as the instance reports it, under the identifier it issued.</summary>
    /// <remarks>
    /// Both the post and the read-back answer this resource, so the two agree on the identifier and
    /// the verb reports the search as one the instance holds.
    /// </remarks>
    private const string PostedCommandRow =
        """{"id":9001,"name":"MoviesSearch","status":"queued"}""";

    /// <summary>One row, as the instance answers a per-scene read for a scene it holds.</summary>
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

    /// <summary>
    /// The body the instance receives carries the scene resource's own suppression flag, present and
    /// false, and names no command at all.
    /// </summary>
    /// <remarks>
    /// Read off the bytes that left rather than off the arguments a seam was handed: what a call site
    /// supplied is not what the client composes from it, and the composed body is what an instance
    /// acts on.
    /// </remarks>
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

    /// <summary>An answer that stops part way through reads as having reached the instance not at all.</summary>
    /// <remarks>
    /// The body is read out of the response stream, so a connection dropped mid-answer raises an I/O
    /// failure rather than a request one, and this product must contain both.
    /// </remarks>
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

    /// <summary>
    /// The search sends one command, and it carries the name and the id member the instance honours.
    /// </summary>
    /// <remarks>
    /// The command name is a measurement rather than a convention, taken against a running instance
    /// of build 3.4.0.1387: <c>MoviesSearch</c> answers 201 and echoes its <c>movieIds</c> member,
    /// while <c>SceneSearch</c>, <c>MovieSearch</c> and a name nothing registers each answer 400 with
    /// "Unknown command type". A wrong id member is accepted and dropped, so the command then runs
    /// over nothing and a body naming one would look like a working search.
    /// <para>
    /// Read off the bytes that left rather than off the arguments a seam was handed: what a call site
    /// supplied is not what the client composes from it.
    /// </para>
    /// </remarks>
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

    /// <summary>The scene is read before the command, and the command is read after it.</summary>
    /// <remarks>
    /// The command names the instance's own identifier and the browser holds only the provider's, so
    /// a command sent first would name an identifier nothing had answered. The read that follows is
    /// the command read back by the identifier the instance issued.
    /// </remarks>
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

    /// <summary>
    /// A scene the instance holds no entry for is stated as an absence, and no command is sent.
    /// </summary>
    /// <remarks>
    /// Both spellings the instance can state it in: an empty row set, and a not-found. Neither is a
    /// failure, so the answer carries the state the instance really holds.
    /// </remarks>
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

    /// <summary>The answer carries the state read off the scene's own row.</summary>
    /// <remarks>
    /// A search changes what the instance is looking for and not what it holds, so an unmonitored
    /// scene reads back unmonitored.
    /// </remarks>
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

    /// <summary>A row carrying no usable identifier is the instance declining, and nothing is sent.</summary>
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

    /// <summary>A read that answered nothing readable reads as having reached the instance not at all.</summary>
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

    /// <summary>A command the instance declined reads as the instance refusing it.</summary>
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

    /// <summary>A search with nothing connected reaches the instance not at all.</summary>
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

    /// <summary>
    /// A generation registering no role for the verb has nothing to hand over, so nothing is sent
    /// and the answer names the absent role. Reported as the instance declining, it would blame an
    /// instance that was never asked.
    /// </summary>
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

    /// <summary>The search reaches a grabbing verb and the monitor verb reaches none.</summary>
    /// <remarks>
    /// Both verbs are driven on one host, so the classes are read off one ordered log rather than off
    /// two that could never have held each other's calls.
    /// </remarks>
    [Fact]
    public async Task TheSearchVerbIsClassedGrabbingAndTheMonitorVerbIsNot()
    {
        await using var host = await MonitorHost.CreateAsync();
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

    /// <summary>A caller who cannot configure the extension reaches neither per-scene verb.</summary>
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
