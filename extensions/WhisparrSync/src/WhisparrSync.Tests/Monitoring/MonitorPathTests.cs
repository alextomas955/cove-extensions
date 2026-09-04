using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.Invariants;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The whole studio monitor path, driven through the mapped route rather than through a handler
/// method: the stored identity row, the add defaults read from the instance, the composed body and
/// the ordered list of what left for the instance.
/// </summary>
/// <remarks>
/// Every case runs against the double that records the ARGUMENTS of every outbound request, and each
/// emptiness assertion is paired with a send through the SAME double, so an empty log is evidence
/// rather than the only thing this class could report.
/// <para>
/// The route is driven over a test server, so the route pattern, the kind parse, the body binding and
/// the declared gate are the shipped ones. A test calling the handler method directly would agree
/// with a route mounted at the wrong pattern, bound to a body the browser cannot send, or reachable
/// by a caller the declaration excludes.
/// </para>
/// <para>
/// What the instance received is read off a stub message handler under a real client, not off the
/// recording double: the double stands in at the role seam, above the point the body is composed, so
/// a body assertion taken there would be an assertion about the test.
/// </para>
/// </remarks>
public sealed class MonitorPathTests
{
    /// <summary>A stored identifier of the shape the older generation's source mints.</summary>
    private const string V2StoredIdentifier = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    /// <summary>One site as the older generation's lookup answers with it.</summary>
    private const string V2OneSite = """
        [{"tvdbId":3372,"title":"Vixen","titleSlug":"vixen","year":2016}]
        """;

    /// <summary>
    /// The whole gesture: one stored identity row in, one monitored studio out, and nothing that
    /// could make the instance acquire anything at any position in the sequence.
    /// </summary>
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

    /// <summary>
    /// Both of this generation's search-suppressing spellings are PRESENT and false in the bytes the
    /// instance receives, and the fields its database requires are there too.
    /// </summary>
    /// <remarks>
    /// Presence is asserted separately from value. An absent member and a false one read the same off
    /// a deserialized object, and the generation's own default for the absent case is what this
    /// product must never depend on.
    /// </remarks>
    [Fact]
    public async Task TheAddTheInstanceReceivesCarriesBothSuppressionFlagsPresentAndFalse()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, MonitorHost.AddedStudio);
        using var http = new HttpClient(handler);

        await ((IWhisparrStudioActing)new WhisparrClient(http, NullLogger.Instance)).AddMonitoredStudioAsync(
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

        var addOptions = Assert.IsType<JsonObject>(body["addOptions"]);
        Assert.True(addOptions.ContainsKey("searchForMovie"));
        Assert.False(addOptions["searchForMovie"]!.GetValue<bool>());

        // Both NOT NULL columns with no rule set in front of them: a missing one is answered with a
        // raw database message rather than a validation failure.
        Assert.Equal("/config/library", body["rootFolderPath"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(body["tags"]));

        Assert.True(body.ContainsKey("afterDate"));
        Assert.True(body["monitored"]!.GetValue<bool>());
        Assert.False(body["moviesMonitored"]!.GetValue<bool>());
        Assert.False(addOptions["moviesMonitored"]!.GetValue<bool>());
        Assert.Equal(4, body["qualityProfileId"]!.GetValue<int>());
    }

    /// <summary>
    /// A monitor request naming no scope is carried out at the STORED default, not at a literal in
    /// the acting path.
    /// </summary>
    /// <remarks>
    /// Presence of the date gate is the whole assertion, because its absence is how the wider scope
    /// is expressed: this generation's own help text says an empty value is ignored, so there is no
    /// value that states the wider scope and omission is the statement.
    /// </remarks>
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

    /// <summary>A scope the request names wins over the stored default.</summary>
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

    /// <summary>
    /// The bulk path's per-entity step reads the same stored default, so a selection cannot behave
    /// differently from a click.
    /// </summary>
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

    /// <summary>
    /// The older generation is asked under the stored identifier exactly as the library holds it, and
    /// the entity is then added under the numeric identifier the lookup answered with.
    /// </summary>
    /// <remarks>
    /// The prefixed spelling is answered with a success and an empty list, so a term carrying one
    /// would compose no add at all and report nothing wrong. The query is what this asserts, because
    /// that is where the term travels.
    /// </remarks>
    [Fact]
    public async Task TheOlderGenerationIsAskedUnprefixedAndAddedUnderTheIdentifierItAnsweredWith()
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, V2OneSite), (HttpStatusCode.Created, "{\"id\":1}"));
        using var http = new HttpClient(handler);

        await ((IWhisparrStudioActing)new WhisparrClient(http, NullLogger.Instance))
            .AddMonitoredStudioAsync(
                new Uri(MonitorHost.StoredAddress),
                MonitorHost.StoredKey,
                WhisparrGeneration.V2,
                V2StoredIdentifier,
                MonitorScope.FutureScenes,
                new AddDefaults(1, "/config/library"),
                TestCt);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/v3/series/lookup", handler.Requests[0].Path);
        Assert.Equal("/api/v3/series/lookup?term=" + V2StoredIdentifier, handler.Targets[0]);
        Assert.DoesNotContain("tpdb", handler.Targets[0], StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("/api/v3/series", handler.Targets[1]);

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests[1].Body));
        Assert.Equal(3372, body["tvdbId"]!.GetValue<int>());
        Assert.DoesNotContain(V2StoredIdentifier, handler.Requests[1].Body, StringComparison.Ordinal);

        var addOptions = Assert.IsType<JsonObject>(body["addOptions"]);
        Assert.Equal("future", addOptions["monitor"]!.GetValue<string>());
        Assert.False(addOptions["searchForMissingEpisodes"]!.GetValue<bool>());
        Assert.False(addOptions["searchForCutoffUnmetEpisodes"]!.GetValue<bool>());
        Assert.Equal(1, body["qualityProfileId"]!.GetValue<int>());
    }

    /// <summary>
    /// A lookup naming more than one entity stops the gesture with nothing composed and nothing sent
    /// after it.
    /// </summary>
    /// <remarks>
    /// Paired with the case above, which sends a second request through the same stub, so the single
    /// recorded request here is evidence about the refusal rather than about the stub.
    /// </remarks>
    [Fact]
    public async Task ALookupNamingMoreThanOneEntityAddsNothing()
    {
        const string twoSites = """
            [{"tvdbId":3372,"title":"Vixen","titleSlug":"vixen"},
             {"tvdbId":36826,"title":"Vixen Media Group","titleSlug":"vixen-media-group"}]
            """;

        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, twoSites), (HttpStatusCode.Created, "{\"id\":1}"));
        using var http = new HttpClient(handler);

        var answered = await ((IWhisparrStudioActing)new WhisparrClient(http, NullLogger.Instance))
            .AddMonitoredStudioAsync(
                new Uri(MonitorHost.StoredAddress),
                MonitorHost.StoredKey,
                WhisparrGeneration.V2,
                V2StoredIdentifier,
                MonitorScope.AllScenes,
                new AddDefaults(1, "/config/library"),
                TestCt);

        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
        Assert.NotEqual(
            MonitorRefusalKind.None, MonitoringProjector.Accepted(answered));
    }

    /// <summary>
    /// Whether the older generation's instance holds the entity is read out of its own listing, and
    /// only the matched entry is carried onward.
    /// </summary>
    [Fact]
    public async Task TheOlderGenerationsHeldReadingComesFromItsOwnListing()
    {
        const string listed = """
            [{"id":1,"tvdbId":3372,"title":"Vixen","monitored":true},
             {"id":2,"tvdbId":247,"title":"Tushy Raw","monitored":false}]
            """;

        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, V2OneSite), (HttpStatusCode.OK, listed));
        using var http = new HttpClient(handler);

        var read = await ((IWhisparrStudioActing)new WhisparrClient(http, NullLogger.Instance))
            .ReadStudioAsync(
                new Uri(MonitorHost.StoredAddress),
                MonitorHost.StoredKey,
                WhisparrGeneration.V2,
                V2StoredIdentifier,
                TestCt);

        // The recorded query, never the recorded path: AbsolutePath alone cannot tell a read narrowed
        // to one entity from one that asked the instance for its whole catalogue.
        Assert.Equal("/api/v3/series?tvdbId=3372", handler.Targets[1]);
        Assert.Equal(
            MonitoringProjector.EntityReading.Held, MonitoringProjector.Classify(read).Reading);
        Assert.Equal(1, MonitoringProjector.EntityIdIn(read.Body));
        Assert.True(MonitoringProjector.MonitoredIn(read.Body));
        Assert.DoesNotContain("Tushy Raw", read.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// An entity the older generation's instance lists nowhere reads as not held, which is not a
    /// refusal: it is the precondition for adding it.
    /// </summary>
    [Fact]
    public async Task AnEntityTheOlderGenerationDoesNotListReadsAsNotHeld()
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, V2OneSite), (HttpStatusCode.OK, "[]"));
        using var http = new HttpClient(handler);

        var read = await ((IWhisparrStudioActing)new WhisparrClient(http, NullLogger.Instance))
            .ReadStudioAsync(
                new Uri(MonitorHost.StoredAddress),
                MonitorHost.StoredKey,
                WhisparrGeneration.V2,
                V2StoredIdentifier,
                TestCt);

        Assert.Equal(
            MonitoringProjector.EntityReading.NotHeld, MonitoringProjector.Classify(read).Reading);
    }

    /// <summary>
    /// The flag flip and the scope change reach this generation's own routes with its own bodies, and
    /// neither reads anything first.
    /// </summary>
    /// <remarks>
    /// The scope change is one request here and a read-then-replace on the other generation, because
    /// this one re-applies the option over what the instance already holds while the other's editor
    /// resource declares no gate at all.
    /// </remarks>
    [Fact]
    public async Task TheOlderGenerationsFlipAndScopeChangeReachItsOwnRoutes()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Accepted, "{}");
        using var http = new HttpClient(handler);
        var client = new WhisparrClient(http, NullLogger.Instance);
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

    /// <summary>An identifier a caller put in the body reaches nothing.</summary>
    /// <remarks>
    /// The request shape declares no identifier field at all, so this drives the wire rather than the
    /// record: a member the model drops is exactly what a caller would try.
    /// </remarks>
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

    /// <summary>A studio carrying no identity in the connected generation's namespace.</summary>
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

    /// <summary>An identity row in the OTHER generation's namespace is not one in this one.</summary>
    [Fact]
    public async Task AStudioIdentifiedOnlyInTheOtherNamespaceRefusesBeforeAnythingIsSent()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync("theporndb.net/graphql", MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>An instance offering no profile at all.</summary>
    /// <remarks>
    /// The stop is the only guard on this generation: it accepts a zero profile id, echoes it back,
    /// and the studio then monitors happily and can never acquire anything.
    /// </remarks>
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

    /// <summary>An instance whose first offered profile carries the id its own add would accept.</summary>
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

    /// <summary>An instance offering no root folder, which a fresh one does.</summary>
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

    /// <summary>The profile chosen is the first the instance offered, in the order received.</summary>
    /// <remarks>
    /// The offered list is deliberately not in id order, so a sort anywhere on the path changes the
    /// answer and is reported here.
    /// </remarks>
    [Fact]
    public void TheProfileChosenIsTheFirstOfferedAndNotTheLowestId()
    {
        var resolved = AddDefaultsProjector.From(MonitorHost.UnsortedProfiles, MonitorHost.OneRootFolder);

        Assert.Equal(MonitorRefusalKind.None, resolved.Refusal);
        Assert.Equal(4, resolved.Defaults?.QualityProfileId);
        Assert.Equal("/config/library", resolved.Defaults?.RootFolderPath);
    }

    /// <summary>An entity the instance already holds keeps its own defaults, which are never read.</summary>
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

    /// <summary>
    /// The mount read answers what the entity's state IS, and which capabilities the connected
    /// generation holds, so the browser carries no generation table of its own.
    /// </summary>
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

    /// <summary>A studio the instance does not hold reads as not monitored rather than as a failure.</summary>
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

    /// <summary>
    /// The mount read carries the scope the instance's own answer reported, and carries none where
    /// that answer reported nothing.
    /// </summary>
    /// <remarks>
    /// Driven through the mapped route rather than through the projection, because the defect this
    /// closes was the browser reading a scope the read never carried.
    /// </remarks>
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

    /// <summary>
    /// A studio the instance holds and does not monitor reports no scope, whatever date gate its
    /// record still carries.
    /// </summary>
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

    /// <summary>A performer reports no scope, because it expresses none on either generation.</summary>
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

    /// <summary>The scope-change route answers the scope it just applied.</summary>
    [Fact]
    public async Task TheScopeChangeAnswersTheScopeItApplied()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            Json(200, """{"id":1,"monitored":true,"afterDate":"2026-09-03"}"""));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.ChangeScopeAsync("studio", studioId, "allScenes");

        Assert.Equal(MonitorScope.AllScenes, view.Scope);
    }

    /// <summary>Unmonitoring leaves no scope in force to report.</summary>
    [Fact]
    public async Task UnmonitoringAnswersNoScope()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            Json(200, """{"id":1,"monitored":true,"afterDate":"2026-09-03"}"""));
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.UnmonitorAsync("studio", studioId);

        Assert.False(view.Monitored);
        Assert.Null(view.Scope);
    }

    /// <summary>An instance refusing the add is reported as a kind, never as its own words.</summary>
    /// <remarks>
    /// This generation answers a failed add with a body carrying a full stack trace under
    /// <c>description</c>. Nothing here reads that member, so nothing it holds can reach a user.
    /// </remarks>
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

    /// <summary>
    /// The same two routes serve a performer, and the kind on the route selects both the identity
    /// table read and the acting member sent.
    /// </summary>
    /// <remarks>
    /// A studio is seeded beside the performer, in the same namespace, under a DIFFERENT stored
    /// identifier and carrying the same cove id: the two kinds number their rows independently. A
    /// path reading the wrong identity table would therefore find a row and send that studio's
    /// identifier, which is what the identifier assertion below refuses.
    /// </remarks>
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

    /// <summary>A performer carrying no identity row refuses before anything is sent.</summary>
    /// <remarks>
    /// Paired with a send through the same double, so the empty log is evidence rather than the only
    /// thing this case could report.
    /// </remarks>
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

    /// <summary>A kind no route segment can be read as is a malformed request, never a default.</summary>
    [Fact]
    public async Task AKindTheRouteCannotBeReadAsIsRefusedAsABadRequest()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.Http.PostAsJsonAsync(
            host.RouteFor("banana", 1, "monitor"), new MonitorEntityRequest(null), TestCt);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>Neither monitoring route answers a caller without the tier it declares.</summary>
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

    /// <summary>An unconfigured connection refuses before anything is sent.</summary>
    [Fact]
    public async Task AnUnconfiguredConnectionRefusesBeforeAnythingIsSent()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);
        var studioId = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NotConfigured, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>
    /// Future Scenes puts the add-time gate in the body and All Scenes leaves it out, and an
    /// unrecognised scope resolves to neither.
    /// </summary>
    /// <remarks>
    /// The instant is supplied rather than read inside the projector, so the spelling under test is
    /// the one the instance was measured accepting rather than whatever today produces.
    /// </remarks>
    [Fact]
    public void TheAddTimeGateIsPresentForFutureScenesAndAbsentForAllScenes()
    {
        var now = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        var defaults = new AddDefaults(4, "/config/library");

        Assert.Equal(
            "2026-09-02T00:00:00Z",
            V3BodyProjector.AddStudio(MonitorHost.StudioRemoteIdValue, MonitorScope.FutureScenes, defaults, now)["afterDate"]
                ?.GetValue<string>());
        Assert.False(
            V3BodyProjector.AddStudio(MonitorHost.StudioRemoteIdValue, MonitorScope.AllScenes, defaults, now)
                .ContainsKey("afterDate"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => V3BodyProjector.AddStudio(MonitorHost.StudioRemoteIdValue, (MonitorScope)(-1), defaults, now));
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static WhisparrResponse Json(int status, string body)
        => RecordingWhisparrClient.Json(status, body);
}
