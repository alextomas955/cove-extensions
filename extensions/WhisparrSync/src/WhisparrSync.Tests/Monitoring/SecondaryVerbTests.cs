using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.Invariants;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// No search is executed here. Both fixture instances report no indexer and no download client, so a
// search that did start could find nothing. The grabbing verb is asserted on the body it composes
// and on the role's reachability. The command payloads are transcribed from the two interface
// bundles, and the array-versus-scalar split is the subject: a cross-lineage payload is accepted
// and does nothing.
public sealed class SecondaryVerbTests
{
    private const string SceneForeignId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static readonly AddDefaults Defaults = new(4, "/config/library");

    // A parent studio profile that differs from the one the instance offers first, so a body copying
    // the parent's is distinguishable from one taking the instance's.
    private const string ParentStudioProfileId = "9";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // Presence is asserted apart from the value: an absent member and a false one read the same off
    // a value, and the instance's default for the absent case is not this product's to rely on.
    [Fact]
    public void TheSceneAddSuppressesAcquisitionAndNamesTheSceneOnlyMonitorTypeAndTheManualAddMethod()
    {
        var body = ComposedBody.Of(V3BodyProjector.AddScene(SceneForeignId, Defaults));
        var addOptions = Assert.IsType<JsonObject>(body["addOptions"]);

        Assert.True(addOptions.ContainsKey("searchForMovie"));
        Assert.False(addOptions["searchForMovie"]!.GetValue<bool>());

        // The scene resource declares its acquisition flag inside the add-options member and declares
        // no top-level one, so a top-level flag here would be a member the instance discards.
        Assert.False(body.ContainsKey("searchOnAdd"));

        Assert.Equal("sceneOnly", addOptions["monitor"]!.GetValue<string>());
        Assert.Equal("manual", addOptions["addMethod"]!.GetValue<string>());
    }

    [Fact]
    public void EveryComposedSceneAddCarriesTheRootTheTagsAndAUsableProfile()
    {
        var body = ComposedBody.Of(V3BodyProjector.AddScene(SceneForeignId, Defaults));

        Assert.Equal(SceneForeignId, body["foreignId"]!.GetValue<string>());
        Assert.Equal("/config/library", body["rootFolderPath"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(body["tags"]));
        Assert.Equal(4, body["qualityProfileId"]!.GetValue<int>());
        Assert.True(body["monitored"]!.GetValue<bool>());

        // The one guard on this generation: it accepts a zero profile, echoes it back, and the scene
        // then monitors and can never acquire anything.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ComposedBody.Of(V3BodyProjector.AddScene(SceneForeignId, new AddDefaults(0, "/config/library"))));
    }

    // Whisparr sets a refresh-created scene's profile from its own studio, so copying one would be
    // this product deciding something the instance owns. Composed beside a parent resource whose
    // profile is a value the instance never offered.
    [Fact]
    public void NoSceneAddCarriesAProfileCopiedFromTheParentStudio()
    {
        var parent = Assert.IsType<JsonObject>(
            JsonNode.Parse(
                $$"""{"id":4,"foreignId":"{{SceneForeignId}}","qualityProfileId":{{ParentStudioProfileId}}}"""));

        var body = ComposedBody.Of(V3BodyProjector.AddScene(SceneForeignId, Defaults));

        Assert.Equal(
            int.Parse(ParentStudioProfileId, System.Globalization.CultureInfo.InvariantCulture),
            parent["qualityProfileId"]!.GetValue<int>());
        Assert.Equal(Defaults.QualityProfileId, body["qualityProfileId"]!.GetValue<int>());
        Assert.DoesNotContain(
            ParentStudioProfileId, body["qualityProfileId"]!.ToJsonString(), StringComparison.Ordinal);
    }

    // The split is real and silent: a cross-lineage payload is answered as created and does nothing,
    // so what is asserted is the composed body rather than any status.
    [Fact]
    public void TheRefreshCommandComposesAnArrayOnV3AndAScalarOnV2()
    {
        var studio = V3BodyProjector.RefreshCatalogue(WhisparrEntityKind.Studio, 1);
        var performer = V3BodyProjector.RefreshCatalogue(WhisparrEntityKind.Performer, 1);
        var series = V2BodyProjector.RefreshCatalogue(1);

        Assert.Equal("RefreshStudios", studio["name"]!.GetValue<string>());
        Assert.Equal([1], Assert.IsType<JsonArray>(studio["studioIds"]).Select(id => id!.GetValue<int>()));
        Assert.Equal("RefreshPerformers", performer["name"]!.GetValue<string>());
        Assert.Equal(
            [1], Assert.IsType<JsonArray>(performer["performerIds"]).Select(id => id!.GetValue<int>()));

        Assert.Equal("RefreshSeries", series["name"]!.GetValue<string>());
        Assert.Null(series["seriesId"] as JsonArray);
        Assert.Equal(1, series["seriesId"]!.GetValue<int>());

        // Neither generation's own spelling appears in the other's body.
        Assert.DoesNotContain("seriesId", studio.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("seriesId", performer.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("studioIds", series.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("performerIds", series.ToJsonString(), StringComparison.Ordinal);
    }

    // No v2 route adds a catalogue item at all, so the capability is absent rather than expressed
    // differently. Redefining it there as a catalogue refresh would read as an action that did
    // nothing.
    [Fact]
    public void V2DeclaresNoSceneRegistrationRole()
    {
        Assert.DoesNotContain(
            typeof(IWhisparrMissingSceneActing), typeof(WhisparrV2Instance).GetInterfaces());
        Assert.DoesNotContain(
            WhisparrCapability.RegisterMissingScenes,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));
    }

    [Fact]
    public void V3DeclaresTheSceneRegistrationRole()
    {
        Assert.Contains(
            typeof(IWhisparrMissingSceneActing), typeof(WhisparrV3Instance).GetInterfaces());
        Assert.Contains(
            WhisparrCapability.RegisterMissingScenes,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V3));
    }

    // Neither generation offers an import mode that only links, so the two cases the verb decides
    // between are identical on each. The role is asserted off each instance rather than off a
    // runtime table, and the declared capability array is asserted to agree. v2 was measured
    // serving all three of the routes this role sends on, at 2.2.0.231.
    [Fact]
    public void BothGenerationsDeclareTheReflectOwnedRole()
    {
        Assert.Contains(
            typeof(IWhisparrReflectOwnedActing), typeof(WhisparrV3Instance).GetInterfaces());
        Assert.Contains(
            typeof(IWhisparrReflectOwnedActing), typeof(WhisparrV2Instance).GetInterfaces());

        foreach (var generation in new[] { WhisparrGeneration.V3, WhisparrGeneration.V2 })
        {
            Assert.Contains(
                WhisparrCapability.ReflectOwnedFiles,
                GenerationCapabilities.CapabilitiesOf(generation));
        }
    }

    // The same three routes on the generation whose own client composes them. A v2 instance holding
    // no arm for this role would have sent nothing at all.
    [Fact]
    public async Task V2ReachesTheSameThreeReflectOwnedRoutes()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var http = new HttpClient(handler);
        var acting = (IWhisparrReflectOwnedActing)TestWhisparrClient.Over(
            http, handler, generation: WhisparrGeneration.V2);

        await acting.ReadHardlinkSettingAsync(TestCt);
        await acting.ListImportableFilesAsync("/config/library/Vixen & Co", TestCt);
        await acting.AttachOwnedFilesAsync(
            new JsonArray(new JsonObject { ["path"] = "/config/library/a.mp4" }), TestCt);

        Assert.Equal(
            ["/api/v3/config/mediamanagement", "/api/v3/manualimport", "/api/v3/command"],
            handler.Requests.Select(request => request.Path));
    }

    // Every index rather than the last: a grab issued before the adds would be just as acquiring.
    // Paired with the acting assertion, so a run that contacted the instance not at all cannot
    // satisfy the emptiness half.
    [Fact]
    public async Task AWholeAddAllMissingRunHoldsNoGrabbingVerbAtAnyPosition()
    {
        var client = Recorder();

        foreach (var scene in new[] { SceneForeignId, "5b1f7c33-0000-4000-8000-0000000000ab" })
        {
            await client.AddSceneAsync(scene, Defaults, TestCt);
        }

        await client.RefreshCatalogueAsync(WhisparrEntityKind.Studio, 1, TestCt);

        Assert.Contains(
            client.Verbs, verb => OutboundSeam.VerbClassByMember[verb] == WhisparrVerbClass.Act);
        Assert.All(
            client.Verbs,
            verb => Assert.NotEqual(WhisparrVerbClass.Grab, OutboundSeam.VerbClassByMember[verb]));

        // One bounded call per scene, driven by the set handed in rather than by anything the library
        // holds, plus the one refresh that makes the registrations visible.
        Assert.Equal(
            2,
            client.Acting.Count(
                call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync)));
        Assert.Single(
            client.Acting,
            call => call.Verb == nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync));
    }

    // Read off a stub below the client rather than off the role double.
    [Fact]
    public async Task TheSceneAddAndTheRefreshReachTheirOwnRoutesWithTheComposedBodies()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":11}""");
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http, handler);

        await ((IWhisparrMissingSceneActing)client).AddSceneAsync(SceneForeignId, Defaults, TestCt);
        await ((IWhisparrMissingSceneActing)client).RefreshCatalogueAsync(WhisparrEntityKind.Studio, 11, TestCt);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v3/movie", handler.Requests[0].Path);
        Assert.Equal("/api/v3/command", handler.Requests[1].Path);

        var add = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests[0].Body));
        Assert.Equal(
            "sceneOnly",
            Assert.IsType<JsonObject>(add["addOptions"])["monitor"]!.GetValue<string>());

        var refresh = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests[1].Body));
        Assert.Equal("RefreshStudios", refresh["name"]!.GetValue<string>());
        Assert.Equal([11], Assert.IsType<JsonArray>(refresh["studioIds"]).Select(id => id!.GetValue<int>()));
    }

    // A per-scene add needs no second shape, and a batch that fails part-way is harder to report
    // honestly than a sequence of per-scene outcomes.
    [Fact]
    public void TheBatchedAddFormIsNotComposableAnywhereInThisProduct()
        => Assert.DoesNotContain(
            "movie/import",
            string.Join('\n', OutboundSeamTypes.DeclaredLiterals()),
            StringComparison.Ordinal);

    // The folder travels as a query value and never as a route segment, so a folder name cannot
    // change which route is issued.
    [Fact]
    public async Task TheReflectOwnedReadsAndTheAttachReachTheirOwnRoutes()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http, handler);

        await ((IWhisparrReflectOwnedActing)client).ReadHardlinkSettingAsync(TestCt);
        await ((IWhisparrReflectOwnedActing)client).ListImportableFilesAsync("/config/library/Vixen & Co", TestCt);
        await ((IWhisparrReflectOwnedActing)client).AttachOwnedFilesAsync(new JsonArray(new JsonObject { ["path"] = "/config/library/a.mp4" }), TestCt);

        Assert.Equal("/api/v3/config/mediamanagement", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);

        // The separator, the space and the ampersand are all escaped, so a directory name carrying
        // one names no other route and starts no second query value. The instance was measured
        // resolving this spelling and the percent-encoded-space spelling to the same folder, so the
        // pin is on escaping having happened rather than on which of the two forms it produced.
        Assert.Equal("/api/v3/manualimport", handler.Requests[1].Path);
        Assert.Contains(
            "folder=%2fconfig%2flibrary%2fVixen+%26+Co", handler.Targets[1], StringComparison.Ordinal);

        Assert.Equal("/api/v3/command", handler.Requests[2].Path);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        var attach = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests[2].Body));
        Assert.Equal("ManualImport", attach["name"]!.GetValue<string>());
        Assert.Equal("copy", attach["importMode"]!.GetValue<string>());
    }

    // The command names and the id array are transcribed from v3's own interface bundle.
    [Fact]
    public void V3SearchNamesEachCommandWithAnIdArray()
    {
        var studio = V3BodyProjector.SearchMonitored(WhisparrEntityKind.Studio, 1);
        var performer = V3BodyProjector.SearchMonitored(WhisparrEntityKind.Performer, 1);

        Assert.Equal("StudiosSearch", studio["name"]!.GetValue<string>());
        Assert.Equal([1], Assert.IsType<JsonArray>(studio["studioIds"]).Select(id => id!.GetValue<int>()));

        Assert.Equal("PerformersSearch", performer["name"]!.GetValue<string>());
        Assert.Equal(
            [1], Assert.IsType<JsonArray>(performer["performerIds"]).Select(id => id!.GetValue<int>()));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => V3BodyProjector.SearchMonitored((WhisparrEntityKind)(-1), 1));
    }

    [Fact]
    public void V2SearchNamesItsCommandWithAScalarIdAndNoArrayReachesIt()
    {
        var series = V2BodyProjector.SearchMonitored(1);

        Assert.Equal("SeriesSearch", series["name"]!.GetValue<string>());
        Assert.Null(series["seriesId"] as JsonArray);
        Assert.Equal(1, series["seriesId"]!.GetValue<int>());
        Assert.DoesNotContain("[", series.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("studioIds", series.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("performerIds", series.ToJsonString(), StringComparison.Ordinal);
    }

    // Read off a stub below the client. A cross-lineage payload is answered as created and does
    // nothing, so a status says nothing about whether the right shape was sent.
    [Fact]
    public async Task EachGenerationsSearchBodyIsChosenInsideTheSeam()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, EmptyEntity);
        using var http = new HttpClient(handler);
        var v3Grabbing = (IWhisparrSearchGrabbing)TestWhisparrClient.Over(http, handler);
        var v2Grabbing = (IWhisparrSearchGrabbing)TestWhisparrClient.Over(
            http, handler, generation: WhisparrGeneration.V2);

        await v3Grabbing.SearchMonitoredAsync(WhisparrEntityKind.Studio, [4], TestCt);
        await v2Grabbing.SearchMonitoredAsync(WhisparrEntityKind.Studio, [3], TestCt);

        Assert.All(handler.Requests, request => Assert.Equal("/api/v3/command", request.Path));

        var v3 = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests[0].Body));
        Assert.Equal("StudiosSearch", v3["name"]!.GetValue<string>());
        Assert.Equal([4], Assert.IsType<JsonArray>(v3["studioIds"]).Select(id => id!.GetValue<int>()));

        var v2 = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests[1].Body));
        Assert.Equal("SeriesSearch", v2["name"]!.GetValue<string>());
        Assert.Equal(3, v2["seriesId"]!.GetValue<int>());

        // A lineage this product does not manage composes nothing rather than defaulting to either
        // generation's shape: there is no instance for it, so no grabbing role can be obtained.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TestWhisparrClient.Over(
                http, handler, generation: (WhisparrGeneration)(-1)));
    }

    // Both managed generations declare the role, so absence is not what keeps a monitoring path
    // from grabbing. A lineage this product does not manage declares nothing at all.
    [Fact]
    public void TheSearchVerbIsReachedThroughItsOwnRoleAndAnUnmanagedLineageDeclaresNone()
    {
        var client = Recorder();

        foreach (var instance in new[] { typeof(WhisparrV3Instance), typeof(WhisparrV2Instance) })
        {
            Assert.Contains(typeof(IWhisparrSearchGrabbing), instance.GetInterfaces());
        }

        foreach (var generation in new[] { WhisparrGeneration.V3, WhisparrGeneration.V2 })
        {
            Assert.Contains(
                WhisparrCapability.SearchMonitored,
                GenerationCapabilities.CapabilitiesOf(generation));
        }

        Assert.Empty(GenerationCapabilities.CapabilitiesOf((WhisparrGeneration)(-1)));
        Assert.Empty(client.Verbs);
    }

    // Read back through the policy a request goes through rather than off the table it reads, with a
    // live implementation standing behind each declaration.
    [Fact]
    public void TheGrabbingClassGetsOneAttemptOverALiveImplementation()
    {
        Assert.Equal(
            WhisparrRetryPolicy.NoRetry, WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Grab));
        Assert.Equal(
            [
                nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync),
                nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync),
            ],
            OutboundSeam.MembersOf(WhisparrVerbClass.Grab));
        Assert.Contains(typeof(IWhisparrSearchGrabbing), typeof(WhisparrV3Instance).GetInterfaces());
        Assert.Contains(
            typeof(IWhisparrSceneSearchGrabbing), typeof(WhisparrV3Instance).GetInterfaces());

        // The separation is stronger on the generation that keeps no scene record: the per-scene
        // search is physically absent from that instance rather than absent from a lookup table.
        Assert.Contains(typeof(IWhisparrSearchGrabbing), typeof(WhisparrV2Instance).GetInterfaces());
        Assert.DoesNotContain(
            typeof(IWhisparrSceneSearchGrabbing), typeof(WhisparrV2Instance).GetInterfaces());
    }

    // Driven through the mounted route rather than through a projector: the function stating the
    // rule is private to the API, so a projector call would assert something no user reaches.
    [Fact]
    public async Task ASearchOnAnEntityTheInstanceDoesNotHoldStatesThatRatherThanTheInstanceRefusing()
    {
        await using var host = await AbsentEntityHost();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var answered = await host.Http.PostAsync(
            host.RouteFor("studio", studioId, "search-all-monitored"), content: null, TestCt);
        answered.EnsureSuccessStatusCode();
        var view = (await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(TestCt))!;

        Assert.Equal(MonitorRefusalKind.InstanceHoldsNoSuchEntity, view.Refusal);
        Assert.DoesNotContain(
            nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync), host.Client.Verbs);
    }

    // The expected kind is written out rather than read off the other route, so the two are pinned
    // separately and a change to one is reported.
    [Fact]
    public async Task AddAllMissingOnAnEntityTheInstanceDoesNotHoldStatesThatRatherThanTheInstanceRefusing()
    {
        await using var host = await AbsentEntityHost();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var enqueued = await host.AddAllMissingViewAsync("studio", studioId);

        Assert.Equal(MonitorRefusalKind.InstanceHoldsNoSuchEntity, enqueued.Refusal);
        Assert.Null(enqueued.JobId);
    }

    private const string EmptyEntity = """{"id":1}""";

    private static Uri Address { get; } = new(MonitorHost.StoredAddress);

    // The instance answers the held read as not holding the entity. The stored 404 is the instance's
    // own answer rather than one this product composed, which is what makes the reading a fact read
    // off the wire.
    private static RecordingWhisparrV3Client Recorder()
        => new(RecordingWhisparrCore.Json(201, """{"id":11}"""));

    private static async Task<MonitorHost> AbsentEntityHost()
    {
        var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync), MonitorHost.Json(404, EmptyEntity));
        return host;
    }

}
