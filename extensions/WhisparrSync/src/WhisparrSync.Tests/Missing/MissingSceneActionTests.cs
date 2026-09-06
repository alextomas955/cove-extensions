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
