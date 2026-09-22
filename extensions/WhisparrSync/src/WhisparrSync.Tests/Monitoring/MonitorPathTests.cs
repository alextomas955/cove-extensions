using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.Invariants;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// The whole studio monitor path, driven through the mapped route rather than through a handler
// method. Every case runs against the double that records the arguments of every outbound
// request, and each emptiness assertion is paired with a send through the same double.
// The route is driven over a test server, so the route pattern, the kind parse, the body binding
// and the declared gate are the shipped ones. A test calling the handler method directly would
// agree with a route mounted at the wrong pattern or reachable by a caller the declaration
// excludes.
// What the instance received is read off a stub message handler under a real client, not off the
// recording double: the double stands in above the point the body is composed, so a body
// assertion taken there would be an assertion about the test.
public sealed class MonitorPathTests
{
    // A stored identifier of the shape v2's source mints.
    private const string V2StoredIdentifier = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    // The number the metadata source names that site by.
    private const int V2SiteNumber = 3372;

    private static WhisparrClient V2Client(HttpClient http, BodyRecordingHandler handler)
        => TestWhisparrClient.Over(
            http, handler, siteNumbers: TestSiteNumbers.Numbering(V2StoredIdentifier, V2SiteNumber));

    [Fact]
    public async Task OneStoredIdentityRowMonitorsTheStudioAndStartsNoSearch()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.True(view.Monitored);
        Assert.Equal(WhisparrGeneration.V3, view.Generation);
        Assert.Equal(WhisparrEntityKind.Studio, view.Kind);

        // The entity is read first, because an entity the instance already holds keeps its own
        // defaults and reading them would only invite sending them.
        Assert.Equal(
            [
                nameof(IWhisparrStudioActing.ReadStudioAsync),
                nameof(IWhisparrClient.ReadQualityProfilesAsync),
                nameof(IWhisparrClient.ReadRootFoldersAsync),
                nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
                nameof(IWhisparrStudioActing.ReadStudioAsync),
            ],
            host.Client.Verbs);

        // Checked at every position rather than only at the last: a grab issued before the add would
        // be just as acquiring, and an assertion on the final entry would not see it.
        Assert.DoesNotContain(
            host.Client.Verbs,
            verb => OutboundSeam.VerbClassByMember.GetValueOrDefault(verb) == WhisparrVerbClass.Grab);

        var add = host.Client.Acting.Single(
            call => call.Verb == nameof(IWhisparrStudioActing.AddMonitoredStudioAsync));
        Assert.Equal(MonitorHost.StudioRemoteIdValue, add.ForeignId);
        Assert.Equal(MonitorScope.FutureScenes, add.Scope);
        Assert.Equal(4, add.Defaults?.QualityProfileId);
        Assert.Equal("/config/library", add.Defaults?.RootFolderPath);
    }

    // Presence is asserted separately from value. An absent member and a false one read the same
    // off a deserialized object, and the generation's own default for the absent case is what
    // this product must never depend on.
    [Fact]
    public async Task TheAddTheInstanceReceivesCarriesTheSuppressionFlagItsResourceDeclares()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, MonitorHost.AddedStudio);
        using var http = new HttpClient(handler);

        await ((IWhisparrStudioActing)TestWhisparrClient.Over(http, handler)).AddMonitoredStudioAsync(
            new Uri(MonitorHost.StoredAddress),
            MonitorHost.StoredKey,
            WhisparrGeneration.V3,
            MonitorHost.StudioRemoteIdValue,
            MonitorScope.FutureScenes,
            new AddDefaults(4, "/config/library"),
            TestCt);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v3/studio", request.Path);

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(request.Body));
        Assert.True(body.ContainsKey("searchOnAdd"));
        Assert.False(body["searchOnAdd"]!.GetValue<bool>());

        // The studio resource declares no add-options member, so one on the wire would be a member
        // the instance discards and this product would be reading a suppression it never applied.
        Assert.False(body.ContainsKey("addOptions"));

        // Both NOT NULL columns with no rule set in front of them: a missing one is answered with a
        // raw database message rather than a validation failure.
        Assert.Equal("/config/library", body["rootFolderPath"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(body["tags"]));

        Assert.True(body.ContainsKey("afterDate"));
        Assert.True(body["monitored"]!.GetValue<bool>());
        Assert.False(body["moviesMonitored"]!.GetValue<bool>());
        Assert.Equal(4, body["qualityProfileId"]!.GetValue<int>());
    }

    // Presence of the date gate is the whole assertion, because its absence is how the wider
    // scope is expressed. This generation ignores an empty value, so omission is the statement.
    [Theory]
    [InlineData(MonitorScope.AllScenes, false)]
    [InlineData(MonitorScope.FutureScenes, true)]
    public async Task AMonitorNamingNoScopeIsCarriedOutAtTheStoredDefault(
        MonitorScope stored, bool carriesTheDateGate)
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.OK, MonitorHost.UnsortedProfiles),
            (HttpStatusCode.OK, MonitorHost.OneRootFolder),
            (HttpStatusCode.Created, MonitorHost.AddedStudio));
        await using var host = await MonitorHost.CreateAsync(bytes: handler, defaultScope: stored);
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        await host.MonitorRawAsync(studioId, "{}");

        var add = Assert.Single(handler.Requests, request => request.Path == "/api/v3/studio");
        var body = Assert.IsType<JsonObject>(JsonNode.Parse(add.Body));
        Assert.Equal(carriesTheDateGate, body.ContainsKey("afterDate"));
    }

    [Fact]
    public async Task AScopeTheRequestNamesWinsOverTheStoredDefault()
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.OK, MonitorHost.UnsortedProfiles),
            (HttpStatusCode.OK, MonitorHost.OneRootFolder),
            (HttpStatusCode.Created, MonitorHost.AddedStudio));
        await using var host = await MonitorHost.CreateAsync(
            bytes: handler, defaultScope: MonitorScope.AllScenes);
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        await host.MonitorRawAsync(studioId, """{"scope":"futureScenes"}""");

        var add = Assert.Single(handler.Requests, request => request.Path == "/api/v3/studio");
        Assert.True(Assert.IsType<JsonObject>(JsonNode.Parse(add.Body)).ContainsKey("afterDate"));
    }

    // The bulk path's per-entity step reads the same stored default, so a selection cannot behave
    // differently from a click.
    [Theory]
    [InlineData(MonitorScope.AllScenes, false)]
    [InlineData(MonitorScope.FutureScenes, true)]
    public async Task ABulkMonitorNamingNoScopeIsCarriedOutAtTheStoredDefault(
        MonitorScope stored, bool carriesTheDateGate)
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.OK, MonitorHost.UnsortedProfiles),
            (HttpStatusCode.OK, MonitorHost.OneRootFolder),
            (HttpStatusCode.Created, MonitorHost.AddedStudio));
        await using var host = await MonitorHost.CreateAsync(bytes: handler, defaultScope: stored);
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var enqueued = await host.PostBulkAsync(
            $$"""{"entityType":"studios","verb":"monitor","entityIds":[{{studioId}}]}""");
        enqueued.EnsureSuccessStatusCode();
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        var add = Assert.Single(handler.Requests, request => request.Path == "/api/v3/studio");
        var body = Assert.IsType<JsonObject>(JsonNode.Parse(add.Body));
        Assert.Equal(carriesTheDateGate, body.ContainsKey("afterDate"));
    }

    // Every other site that answers the not-held kind reads before anything is sent, so its
    // sentence says there was nothing to act on. Here the add left and was accepted, so that
    // sentence would tell a reader nothing happened when the entity may now exist. This refusal is
    // reachable only after a write, and it is driven through the mounted route because the
    // function that chooses it is private to the API.
    [Fact]
    public async Task AnAcceptedAddWhoseReadBackFindsNothingSaysTheChangeWasNotReported()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync), MonitorHost.Json(404, string.Empty));
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studio);

        Assert.Equal(MonitorRefusalKind.InstanceDidNotReportTheChange, view.Refusal);
        Assert.False(view.Monitored);
        Assert.Contains(nameof(IWhisparrStudioActing.AddMonitoredStudioAsync), host.Client.Verbs);
    }

    // An answer past this product's own read bound names this product as the party that stopped,
    // and that stays true after an accepted write. Collapsing every read-back disagreement into
    // one kind would lose it.
    [Fact]
    public async Task AnAcceptedAddWhoseReadBackIsPastTheReadBoundKeepsThatReason()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(404, string.Empty),
            new WhisparrResponse(200, "application/json", string.Empty)
            {
                Refusal = MonitorRefusalKind.AnswerTooLargeToRead,
            });
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studio);

        Assert.Equal(MonitorRefusalKind.AnswerTooLargeToRead, view.Refusal);
    }

    // An answer past the bound arrives with the success status the instance gave and an empty
    // body. Parsing that body reports the site as one the instance does not hold, so this
    // generation's path needs its own case rather than resting on the transport one.
    [Fact]
    public async Task AnAnswerPastTheBoundOnV2ReadKeepsItsOwnReason()
    {
        var past = $"[\"{new string('a', (int)WhisparrTransport.MaxResponseBytes)}\"]";
        var handler = BodyRecordingHandler.AnsweringInTurn((HttpStatusCode.OK, past));
        using var http = new HttpClient(handler);

        var read = await ((IWhisparrStudioActing)V2Client(http, handler))
            .ReadStudioAsync(
                new Uri(MonitorHost.StoredAddress),
                MonitorHost.StoredKey,
                WhisparrGeneration.V2,
                V2StoredIdentifier,
                TestCt);

        Assert.Equal(
            MonitorRefusalKind.AnswerTooLargeToRead, MonitoringProjector.Classify(read).Refusal);
        Assert.Empty(read.Body);
    }

    // The answer is generated here rather than captured, because it is an input for a size
    // property and not a response any instance sent. Every file in the fixtures directory is
    // verbatim.
    // Driven through the transport double rather than through the role seam, so the two-request
    // assembly itself is what runs. A double standing in at the seam answers the assembled reading
    // and never assembles one.
    [Fact]
    public async Task V2HeldReadCarriesOneSiteOnwardHoweverMuchTheInstanceAnswered()
    {
        // The site asked about leads, which is the order the lookup ranks an exact identifier in.
        var listing = new JsonArray
        {
            new JsonObject
            {
                ["id"] = 3373,
                ["tvdbId"] = 3372,
                ["title"] = "Vixen",
                ["monitored"] = true,
            },
        };

        for (var entry = 1; entry <= 20_000; entry += 1)
        {
            // Numbered from a base the resolved entity's own id sits below, so no further entry can
            // carry it and the row above is the only match there is.
            var unmatched = entry + 100_000;
            listing.Add(new JsonObject
            {
                ["id"] = unmatched,
                ["tvdbId"] = unmatched,
                ["title"] = string.Create(CultureInfo.InvariantCulture, $"Entry {unmatched}"),
            });
        }

        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, listing.ToJsonString()));
        using var http = new HttpClient(handler);

        var read = await ((IWhisparrStudioActing)V2Client(http, handler))
            .ReadStudioAsync(
                new Uri(MonitorHost.StoredAddress),
                MonitorHost.StoredKey,
                WhisparrGeneration.V2,
                V2StoredIdentifier,
                TestCt);

        Assert.StartsWith(
            "/api/v3/series/lookup", Assert.Single(handler.Targets), StringComparison.Ordinal);
        Assert.Equal(
            3372,
            Assert.IsType<JsonObject>(JsonNode.Parse(read.Body))["tvdbId"]!.GetValue<int>());
        Assert.Equal(3373, MonitoringProjector.EntityIdIn(read.Body));

        // The whole answer is over a megabyte. Only the matched entry reaches a caller, so what a
        // caller holds does not vary with how much the instance does.
        Assert.True(
            read.Body.Length < 4096,
            $"the read carried {read.Body.Length} characters onward out of a {listing.ToJsonString().Length}-character answer.");
    }

    // The answer is the one the pinned build sent for an identifier it holds no site under: a row
    // mapped from the metadata source, carrying no id of the instance's own. The reading is the
    // precondition for adding the entity, not a report about the instance.
    [Fact]
    public async Task V2HeldReadOfAnEntityTheInstanceDoesNotHoldIsTheFilteredAnswer()
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK,
                ProbeFixtures.Read("whisparr-v2-2.2.0.231-series-lookup-not-held.json")));
        using var http = new HttpClient(handler);

        var read = await ((IWhisparrStudioActing)V2Client(http, handler))
            .ReadStudioAsync(
                new Uri(MonitorHost.StoredAddress),
                MonitorHost.StoredKey,
                WhisparrGeneration.V2,
                V2StoredIdentifier,
                TestCt);

        Assert.StartsWith(
            "/api/v3/series/lookup", Assert.Single(handler.Targets), StringComparison.Ordinal);
        Assert.Equal(
            MonitoringProjector.EntityReading.NotHeld, MonitoringProjector.Classify(read).Reading);
        Assert.Equal(MonitorRefusalKind.None, MonitoringProjector.Classify(read).Refusal);
    }

    // The scope change is one request here and a read-then-replace on the other generation,
    // because this one re-applies the option over what the instance already holds while the
    // other's editor resource declares no gate at all.
    [Fact]
    public async Task V2FlipAndScopeChangeReachItsOwnRoutes()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Accepted, "{}");
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http, handler);
        var address = new Uri(MonitorHost.StoredAddress);

        await ((IWhisparrStudioActing)client).SetStudioMonitoredAsync(
            address, MonitorHost.StoredKey, WhisparrGeneration.V2, 1, monitored: true, TestCt);
        await ((IWhisparrStudioActing)client).SetStudioScopeAsync(
            address, MonitorHost.StoredKey, WhisparrGeneration.V2, 1, MonitorScope.AllScenes, TestCt);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/v3/series/editor", handler.Requests[0].Path);
        Assert.Equal(
            ["monitored", "seriesIds"],
            Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests[0].Body))
                .Select(member => member.Key)
                .Order());

        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("/api/v3/seasonpass", handler.Targets[1]);
        var scope = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests[1].Body));
        Assert.Equal(
            1,
            Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(scope["series"])))["id"]!
                .GetValue<int>());
        Assert.Equal(
            "all", ((JsonObject)scope["monitoringOptions"]!)["monitor"]!.GetValue<string>());
    }

    // The request shape declares no identifier field at all, so this drives the wire rather than
    // the record: a member the model drops is exactly what a caller would try.
    [Fact]
    public async Task AnIdentifierInTheRequestBodyIsIgnoredAndTheStoredRowIsWhatIsSent()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorRawAsync(
            studioId,
            """
            {"scope":"futureScenes","foreignId":"00000000-0000-4000-8000-000000000000",
             "remoteId":"00000000-0000-4000-8000-000000000000","studioId":9999}
            """);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);

        var add = host.Client.Acting.Single(
            call => call.Verb == nameof(IWhisparrStudioActing.AddMonitoredStudioAsync));
        Assert.Equal(MonitorHost.StudioRemoteIdValue, add.ForeignId);
    }

    [Fact]
    public async Task AStudioWithNoStoredRowRefusesBeforeAnythingIsSent()
    {
        await using var host = await MonitorHost.CreateAsync();
        var unidentified = await host.SeedStudioAsync(endpoint: null, remoteId: null);

        var view = await host.MonitorAsync(unidentified);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.False(view.Monitored);
        Assert.Empty(host.Client.Verbs);

        // The same double, driven down a path that does send.
        var identified = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        Assert.Equal(MonitorRefusalKind.None, (await host.MonitorAsync(identified)).Refusal);
        Assert.NotEmpty(host.Client.Verbs);
    }

    [Fact]
    public async Task AStudioIdentifiedOnlyInTheOtherNamespaceRefusesBeforeAnythingIsSent()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync("theporndb.net/graphql", MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    // The stop is the only guard on this generation: it accepts a zero profile id, echoes it back,
    // and the studio then monitors and can never acquire anything.
    [Fact]
    public async Task AnInstanceOfferingNoProfileRefusesWithTheAddNeverSent()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(IWhisparrClient.ReadQualityProfilesAsync), Json(200, "[]"));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NoQualityProfile, view.Refusal);
        Assert.False(view.Monitored);
        Assert.DoesNotContain(nameof(IWhisparrStudioActing.AddMonitoredStudioAsync), host.Client.Verbs);
        Assert.DoesNotContain(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrStudioActing.AddMonitoredStudioAsync));
    }

    [Fact]
    public async Task AProfileIdOfZeroIsRefusedRatherThanSent()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrClient.ReadQualityProfilesAsync), Json(200, """[{"id":0,"name":"Any"}]"""));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NoQualityProfile, view.Refusal);
        Assert.DoesNotContain(nameof(IWhisparrStudioActing.AddMonitoredStudioAsync), host.Client.Verbs);
    }

    // A fresh instance offers no root folder.
    [Fact]
    public async Task AnInstanceOfferingNoRootFolderRefusesWithTheAddNeverSent()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(IWhisparrClient.ReadRootFoldersAsync), Json(200, "[]"));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NoRootFolder, view.Refusal);
        Assert.False(view.Monitored);
        Assert.DoesNotContain(nameof(IWhisparrStudioActing.AddMonitoredStudioAsync), host.Client.Verbs);
    }

    // The offered list is deliberately not in id order, so a sort anywhere on the path changes the
    // answer and is reported here.
    [Fact]
    public void TheProfileChosenIsTheFirstOfferedAndNotTheLowestId()
    {
        var resolved = AddDefaultsProjector.From(MonitorHost.UnsortedProfiles, MonitorHost.OneRootFolder);

        Assert.Equal(MonitorRefusalKind.None, resolved.Refusal);
        Assert.Equal(4, resolved.Defaults?.QualityProfileId);
        Assert.Equal("/config/library", resolved.Defaults?.RootFolderPath);
    }

    [Fact]
    public async Task AStudioTheInstanceAlreadyHoldsIsNotReadForDefaultsAndIsNotAddedAgain()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(IWhisparrStudioActing.ReadStudioAsync), Json(200, MonitorHost.AddedStudio));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.True(view.Monitored);
        Assert.Equal([nameof(IWhisparrStudioActing.ReadStudioAsync)], host.Client.Verbs);
    }

    // The read answers which capabilities the connected generation holds, so the browser carries
    // no generation table of its own.
    [Fact]
    public async Task TheMountReadAnswersTheLiveStateAndTheHeldCapabilities()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(IWhisparrStudioActing.ReadStudioAsync), Json(200, MonitorHost.AddedStudio));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.True(view.Monitored);
        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.Equal(WhisparrGeneration.V3, view.Generation);
        Assert.Contains(WhisparrCapability.MonitorStudio, view.Capabilities);
        Assert.Equal([nameof(IWhisparrStudioActing.ReadStudioAsync)], host.Client.Verbs);
    }

    [Fact]
    public async Task AStudioTheInstanceDoesNotHoldReadsAsNotMonitored()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.False(view.Monitored);
        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.Null(view.Scope);
    }

    // Driven through the mapped route rather than through the projection, because the defect this
    // closes was the browser reading a scope the read never carried.
    [Theory]
    [InlineData("""{"id":1,"monitored":true,"afterDate":"2026-09-03"}""", MonitorScope.FutureScenes)]
    [InlineData("""{"id":1,"monitored":true}""", MonitorScope.AllScenes)]
    public async Task TheMountReadCarriesTheScopeTheInstanceReported(string body, MonitorScope scope)
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(IWhisparrStudioActing.ReadStudioAsync), Json(200, body));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.True(view.Monitored);
        Assert.Equal(scope, view.Scope);
    }

    [Fact]
    public async Task AnUnmonitoredStudioReadCarriesNoScope()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            Json(200, """{"id":1,"monitored":false,"afterDate":"2026-09-03"}"""));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.False(view.Monitored);
        Assert.Null(view.Scope);
    }

    // A performer expresses no scope on either generation.
    [Fact]
    public async Task APerformerReadCarriesNoScope()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrPerformerActing.ReadPerformerAsync),
            Json(200, """{"id":2,"monitored":true,"afterDate":"2026-09-03"}"""));
        var performerId = await host.SeedPerformerAsync(
            MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);

        var answered = await host.Http.GetAsync(
            host.RouteFor("performer", performerId, "monitoring"), TestContext.Current.CancellationToken);
        answered.EnsureSuccessStatusCode();
        var view = (await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(
            TestContext.Current.CancellationToken))!;

        Assert.True(view.Monitored);
        Assert.Null(view.Scope);
    }

    [Fact]
    public async Task TheScopeChangeAnswersTheScopeItApplied()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.SetStudioScopeAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            Json(200, """{"id":1,"monitored":true,"afterDate":"2026-09-03"}"""));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.ChangeScopeAsync("studio", studioId, "allScenes");

        Assert.Equal(MonitorScope.AllScenes, view.Scope);
    }

    [Fact]
    public async Task UnmonitoringAnswersNoScope()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            Json(200, """{"id":1,"monitored":true,"afterDate":"2026-09-03"}"""));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.UnmonitorAsync("studio", studioId);

        Assert.False(view.Monitored);
        Assert.Null(view.Scope);
    }

    // This generation answers a failed add with a body carrying a full stack trace under
    // "description". Nothing here reads that member, so nothing it holds can reach a user.
    [Fact]
    public async Task AnInstanceRefusingTheAddIsReportedAsAKindAndNotAsItsOwnWords()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
            Json(409, """{"message":"constraint failed","description":"at Whisparr.Api.V3 ... "}"""));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.InstanceRefused, view.Refusal);
        Assert.False(view.Monitored);
        Assert.DoesNotContain("Whisparr.Api.V3", JsonSerializer.Serialize(view));
    }

    // A studio is seeded beside the performer, in the same namespace, under a different stored
    // identifier and carrying the same cove id: the two kinds number their rows independently. A
    // path reading the wrong identity table would find a row and send that studio's identifier.
    [Fact]
    public async Task TheKindOnTheRouteSelectsThePerformerTableAndThePerformerAdd()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var performerId = await host.SeedPerformerAsync(MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);
        Assert.Equal(studioId, performerId);

        var view = await host.MonitorAsync("performer", performerId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.True(view.Monitored);
        Assert.Equal(WhisparrEntityKind.Performer, view.Kind);
        Assert.Contains(WhisparrCapability.MonitorPerformer, view.Capabilities);

        Assert.Equal(
            [
                nameof(IWhisparrPerformerActing.ReadPerformerAsync),
                nameof(IWhisparrClient.ReadQualityProfilesAsync),
                nameof(IWhisparrClient.ReadRootFoldersAsync),
                nameof(IWhisparrPerformerActing.AddMonitoredPerformerAsync),
                nameof(IWhisparrPerformerActing.ReadPerformerAsync),
            ],
            host.Client.Verbs);
        Assert.DoesNotContain(nameof(IWhisparrStudioActing.AddMonitoredStudioAsync), host.Client.Verbs);
        Assert.DoesNotContain(
            host.Client.Verbs,
            verb => OutboundSeam.VerbClassByMember.GetValueOrDefault(verb) == WhisparrVerbClass.Grab);

        var add = host.Client.Acting.Single(
            call => call.Verb == nameof(IWhisparrPerformerActing.AddMonitoredPerformerAsync));
        Assert.Equal(MonitorHost.PerformerRemoteIdValue, add.ForeignId);

        // No scope reaches the performer add, because the member declares none to reach.
        Assert.Null(add.Scope);
    }

    // Paired with a send through the same double, so the empty log is evidence.
    [Fact]
    public async Task APerformerWithNoStoredRowRefusesBeforeAnythingIsSent()
    {
        await using var host = await MonitorHost.CreateAsync();
        var unidentified = await host.SeedPerformerAsync(endpoint: null, remoteId: null);

        var view = await host.MonitorAsync("performer", unidentified);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.Equal(WhisparrEntityKind.Performer, view.Kind);
        Assert.Empty(host.Client.Verbs);

        var identified = await host.SeedPerformerAsync(MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);
        Assert.Equal(MonitorRefusalKind.None, (await host.MonitorAsync("performer", identified)).Refusal);
        Assert.NotEmpty(host.Client.Verbs);
    }

    [Fact]
    public async Task AKindTheRouteCannotBeReadAsIsRefusedAsABadRequest()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.Http.PostAsJsonAsync(
            host.RouteFor("banana", 1, "monitor"), new MonitorEntityRequest(null), TestCt);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    [Fact]
    public async Task NeitherMonitoringRouteAnswersACallerWithoutItsTier()
    {
        await using var host = await MonitorHost.CreateAsync(
            principal: FakePrincipalAccessor.None());
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var monitor = await host.Http.PostAsJsonAsync(
            host.RouteFor("studio", studioId, "monitor"),
            new MonitorEntityRequest(MonitorScope.FutureScenes),
            TestCt);
        var read = await host.Http.GetAsync(host.RouteFor("studio", studioId, "monitoring"), TestCt);

        Assert.Equal(HttpStatusCode.Forbidden, monitor.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    [Fact]
    public async Task AnUnconfiguredConnectionRefusesBeforeAnythingIsSent()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NotConfigured, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    // The instant is supplied rather than read inside the projector, so the spelling under test is
    // the one the instance was measured accepting rather than whatever today produces.
    [Fact]
    public void TheAddTimeGateIsPresentForFutureScenesAndAbsentForAllScenes()
    {
        var now = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        var defaults = new AddDefaults(4, "/config/library");

        Assert.Equal(
            "2026-09-02T00:00:00Z",
            ComposedBody.Of(V3BodyProjector.AddStudio(MonitorHost.StudioRemoteIdValue, MonitorScope.FutureScenes, defaults, now))["afterDate"]
                ?.GetValue<string>());
        Assert.False(
            ComposedBody.Of(V3BodyProjector.AddStudio(MonitorHost.StudioRemoteIdValue, MonitorScope.AllScenes, defaults, now))
                .ContainsKey("afterDate"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ComposedBody.Of(V3BodyProjector.AddStudio(MonitorHost.StudioRemoteIdValue, (MonitorScope)(-1), defaults, now)));
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static WhisparrResponse Json(int status, string body)
        => RecordingWhisparrClient.Json(status, body);
}
