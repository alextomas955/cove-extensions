using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.Invariants;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// No search is executed here. Both fixture instances report no indexer and no download client, so
// a search that did start could find nothing, and it would still cost a real instance work if the
// fixture ever gained one. The grabbing verb is asserted on the body it composes and on the role's
// reachability.
//
// The command payloads are transcribed from the two interface bundles. The array-versus-scalar
// split is the subject: a cross-lineage payload is accepted and does nothing.
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
    public void TheOlderGenerationRefusesTheSceneRegistrationRoleByName()
    {
        var refusal = CapabilitiesOn(WhisparrGeneration.V2, Recorder())
            .Obtain<IWhisparrMissingSceneActing>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrCapability.RegisterMissingScenes, refusal.Capability);
        Assert.Equal(WhisparrGeneration.V2, refusal.Generation);
        Assert.DoesNotContain(
            WhisparrCapability.RegisterMissingScenes,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));
    }

    [Fact]
    public void TheNewerGenerationHandsOutTheSceneRegistrationRole()
    {
        var client = Recorder();

        Assert.Same(
            client,
            CapabilitiesOn(WhisparrGeneration.V3, client)
                .Obtain<IWhisparrMissingSceneActing>()
                .Match<IWhisparrMissingSceneActing?>(held => held, _ => null));
    }

    // Neither generation offers an import mode that only links, so the two cases the verb decides
    // between are identical on each.
    [Fact]
    public void BothGenerationsHandOutTheReflectOwnedRole()
    {
        foreach (var generation in new[] { WhisparrGeneration.V3, WhisparrGeneration.V2 })
        {
            var client = Recorder();

            Assert.Contains(
                WhisparrCapability.ReflectOwnedFiles,
                GenerationCapabilities.CapabilitiesOf(generation));
            Assert.Same(
                client,
                CapabilitiesOn(generation, client)
                    .Obtain<IWhisparrReflectOwnedActing>()
                    .Match<IWhisparrReflectOwnedActing?>(held => held, _ => null));
        }
    }

    // Every index rather than the last: a grab issued before the adds would be just as acquiring.
    // Paired with the acting assertion, so a run that contacted the instance not at all cannot
    // satisfy the emptiness half.
    [Fact]
    public async Task AWholeAddAllMissingRunHoldsNoGrabbingVerbAtAnyPosition()
    {
        var client = Recorder();
        var acting = CapabilitiesOn(WhisparrGeneration.V3, client)
            .Obtain<IWhisparrMissingSceneActing>()
            .Match<IWhisparrMissingSceneActing?>(held => held, _ => null);

        Assert.NotNull(acting);
        foreach (var scene in new[] { SceneForeignId, "5b1f7c33-0000-4000-8000-0000000000ab" })
        {
            await acting.AddSceneAsync(Address, Key, scene, Defaults, TestCt);
        }

        await acting.RefreshCatalogueAsync(Address, Key, WhisparrEntityKind.Studio, 1, TestCt);

        Assert.Contains(
            client.Verbs, verb => OutboundSeam.VerbClassByMember[verb] == WhisparrVerbClass.Act);
        Assert.All(
            client.Verbs,
            verb => Assert.NotEqual(WhisparrVerbClass.Grab, OutboundSeam.VerbClassByMember[verb]));

        // One bounded call per scene, driven by the set handed in rather than by anything the library
        // holds, plus the one refresh that makes the registrations visible.
        Assert.Equal(2, client.Acting.Count(call => call.Verb == nameof(acting.AddSceneAsync)));
        Assert.Single(client.Acting, call => call.Verb == nameof(acting.RefreshCatalogueAsync));
    }

    // Read off a stub below the client rather than off the role double.
    [Fact]
    public async Task TheSceneAddAndTheRefreshReachTheirOwnRoutesWithTheComposedBodies()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":11}""");
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http, handler);

        await ((IWhisparrMissingSceneActing)client).AddSceneAsync(
            Address, Key, SceneForeignId, Defaults, TestCt);
        await ((IWhisparrMissingSceneActing)client).RefreshCatalogueAsync(
            Address, Key, WhisparrEntityKind.Studio, 11, TestCt);

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
            string.Join(
                '\n',
                typeof(WhisparrClient)
                    .GetFields(
                        System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Static)
                    .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                    .Select(field => (string?)field.GetRawConstantValue())
                    .OfType<string>()),
            StringComparison.Ordinal);

    // The folder travels as a query value and never as a route segment, so a folder name cannot
    // change which route is issued.
    [Fact]
    public async Task TheReflectOwnedReadsAndTheAttachReachTheirOwnRoutes()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http, handler);

        await ((IWhisparrReflectOwnedActing)client).ReadHardlinkSettingAsync(Address, Key, TestCt);
        await ((IWhisparrReflectOwnedActing)client).ListImportableFilesAsync(
            Address, Key, "/config/library/Vixen & Co", TestCt);
        await ((IWhisparrReflectOwnedActing)client).AttachOwnedFilesAsync(
            Address, Key, new JsonArray(new JsonObject { ["path"] = "/config/library/a.mp4" }), TestCt);

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
    public void TheNewerGenerationsSearchNamesEachCommandWithAnIdArray()
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
    public void TheOlderGenerationsSearchNamesItsCommandWithAScalarIdAndNoArrayReachesIt()
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
        var client = TestWhisparrClient.Over(http, handler);

        await ((IWhisparrSearchGrabbing)client).SearchMonitoredAsync(
            Address, Key, WhisparrGeneration.V3, WhisparrEntityKind.Studio, [4], TestCt);
        await ((IWhisparrSearchGrabbing)client).SearchMonitoredAsync(
            Address, Key, WhisparrGeneration.V2, WhisparrEntityKind.Studio, [3], TestCt);

        Assert.All(handler.Requests, request => Assert.Equal("/api/v3/command", request.Path));

        var v3 = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests[0].Body));
        Assert.Equal("StudiosSearch", v3["name"]!.GetValue<string>());
        Assert.Equal([4], Assert.IsType<JsonArray>(v3["studioIds"]).Select(id => id!.GetValue<int>()));

        var v2 = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests[1].Body));
        Assert.Equal("SeriesSearch", v2["name"]!.GetValue<string>());
        Assert.Equal(3, v2["seriesId"]!.GetValue<int>());

        // A lineage this product does not manage composes nothing rather than defaulting to either
        // generation's shape.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ((IWhisparrSearchGrabbing)client).SearchMonitoredAsync(
                Address, Key, (WhisparrGeneration)(-1), WhisparrEntityKind.Studio, [3], TestCt));
    }

    // The refusal is taken over a lineage this product does not manage. Both managed generations
    // hold the capability, so a refusal over a set built without the role would assert a
    // construction fault instead.
    [Fact]
    public void TheSearchVerbIsReachedThroughItsOwnRoleAndAnAbsentOneIsARefusal()
    {
        var client = Recorder();

        foreach (var generation in new[] { WhisparrGeneration.V3, WhisparrGeneration.V2 })
        {
            Assert.Same(
                client,
                CapabilitiesOn(generation, client)
                    .Obtain<IWhisparrSearchGrabbing>()
                    .Match<IWhisparrSearchGrabbing?>(held => held, _ => null));
        }

        var refusal = GenerationCapabilities.For((WhisparrGeneration)(-1))
            .Obtain<IWhisparrSearchGrabbing>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrCapability.SearchMonitored, refusal.Capability);
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
        Assert.Contains(typeof(IWhisparrSearchGrabbing), typeof(WhisparrClient).GetInterfaces());
        Assert.Contains(typeof(IWhisparrSceneSearchGrabbing), typeof(WhisparrClient).GetInterfaces());
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

    private static string Key => MonitorHost.StoredKey;

    // The instance answers the held read as not holding the entity. The stored 404 is the instance's
    // own answer rather than one this product composed, which is what makes the reading a fact read
    // off the wire.
    private static async Task<MonitorHost> AbsentEntityHost()
    {
        var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync), MonitorHost.Json(404, EmptyEntity));
        return host;
    }

    private static RecordingWhisparrClient Recorder()
        => new(RecordingWhisparrClient.Json(201, """{"id":11}"""));

    private static WhisparrCapabilitySet CapabilitiesOn(
        WhisparrGeneration generation, RecordingWhisparrClient client)
        => GenerationCapabilities.For(generation, WhisparrRoleSet.From(client));
}
