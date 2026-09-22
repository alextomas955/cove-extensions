using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// The whole table is enumerated rather than sampled, and the expected kind per row is written out
// by hand. A table computed from the function it checks agrees with it whatever the order is.
public sealed class RefusalPrecedenceTests
{
    // The identity slot carries the narrowest identity kind. The other identity kinds ride the same
    // slot and are covered separately.
    public static TheoryData<bool, bool, bool, MonitorRefusalKind> EveryCombination => new()
    {
        // no connection, generation gap, no metadata link, then the one kind answered
        { false, false, false, MonitorRefusalKind.None },
        { false, false, true, MonitorRefusalKind.NoIdentityInThisNamespace },
        { false, true, false, MonitorRefusalKind.CapabilityAbsentOnThisGeneration },
        { false, true, true, MonitorRefusalKind.CapabilityAbsentOnThisGeneration },
        { true, false, false, MonitorRefusalKind.NotConfigured },
        { true, false, true, MonitorRefusalKind.NotConfigured },
        { true, true, false, MonitorRefusalKind.NotConfigured },
        { true, true, true, MonitorRefusalKind.NotConfigured },
    };

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void EveryCombinationOfTheThreeReasonsAnswersExactlyOneTranscribedKind(
        bool noConnection, bool generationGap, bool noMetadataLink, MonitorRefusalKind expected)
    {
        var answered = MonitoringProjector.FirstRefusal(new MonitoringProjector.MonitorReasons(
            NoConnectionConfigured: noConnection,
            CapabilityAbsentOnThisGeneration: generationGap,
            IdentityRefusal: noMetadataLink
                ? MonitorRefusalKind.NoIdentityInThisNamespace
                : MonitorRefusalKind.None));

        Assert.Equal(expected, answered);
    }

    // The precedence chooses between the three reasons and never narrows one of them. An identity
    // kind rewritten on the way through would collapse two different sentences into one.
    [Theory]
    [InlineData(MonitorRefusalKind.NoIdentityInThisNamespace)]
    [InlineData(MonitorRefusalKind.SeveralIdentitiesInThisNamespace)]
    public void AnIdentityKindPassesThroughUnchangedWhenNoEarlierReasonHolds(
        MonitorRefusalKind identity)
    {
        Assert.Equal(
            identity,
            MonitoringProjector.FirstRefusal(new MonitoringProjector.MonitorReasons(
                NoConnectionConfigured: false,
                CapabilityAbsentOnThisGeneration: false,
                IdentityRefusal: identity)));
    }

    [Theory]
    [InlineData(MonitorRefusalKind.NoIdentityInThisNamespace)]
    [InlineData(MonitorRefusalKind.SeveralIdentitiesInThisNamespace)]
    public void TheGenerationGapWinsOverEveryIdentityKind(MonitorRefusalKind identity)
    {
        Assert.Equal(
            MonitorRefusalKind.CapabilityAbsentOnThisGeneration,
            MonitoringProjector.FirstRefusal(new MonitoringProjector.MonitorReasons(
                NoConnectionConfigured: false,
                CapabilityAbsentOnThisGeneration: true,
                IdentityRefusal: identity)));
    }

    // Iterates the enum rather than a literal list, so a kind added later fails here until something
    // produces it. The producing set is built by calling the real deciders: a list of names would
    // agree with itself after a decider stopped answering one of them.
    [Fact]
    public async Task EveryDeclaredRefusalKindIsProducedBySomeReachableCase()
    {
        var produced = new HashSet<MonitorRefusalKind>(Reachable())
        {
            await TheKindAnAnswerPastTheReadBoundProducesAsync(),
            await TheKindAnEntityTheInstanceDoesNotHoldProducesAsync(),
            await TheKindAReadBackThatFindsNothingProducesAsync(),
            await TheKindAnEntityWhoseRootAgreedOnNothingProducesAsync(),
        };

        foreach (var kind in Enum.GetValues<MonitorRefusalKind>())
        {
            Assert.Contains(kind, produced);
        }
    }

    // Driven through the mapped route, so what is asserted is the handler's own short-circuit order
    // rather than the pure function being self-consistent. A performer on v2 with nothing configured
    // and no identity row holds all three reasons at once.
    [Fact]
    public async Task AllThreeReasonsAtOnceAnswerTheKindThePrecedenceNames()
    {
        await using var host = await MonitorHost.CreateAsync(
            apiKey: null, generation: WhisparrGeneration.V2);
        var performerId = await host.SeedPerformerAsync(endpoint: null, remoteId: null);

        var view = await host.MonitorAsync("performer", performerId);

        Assert.Equal(
            MonitoringProjector.FirstRefusal(new MonitoringProjector.MonitorReasons(
                NoConnectionConfigured: true,
                CapabilityAbsentOnThisGeneration: true,
                IdentityRefusal: MonitorRefusalKind.NoIdentityInThisNamespace)),
            view.Refusal);
        Assert.Equal(MonitorRefusalKind.NotConfigured, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    // A connection is configured here, so the pair under test is the second and third reasons. v2
    // holds no performer role, so the gap is a real one rather than a set built holding nothing.
    [Fact]
    public async Task AGenerationGapAndNoMetadataLinkTogetherAnswerTheGapAtTheRoute()
    {
        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var performerId = await host.SeedPerformerAsync(endpoint: null, remoteId: null);

        var view = await host.MonitorAsync("performer", performerId);

        Assert.Equal(MonitorRefusalKind.CapabilityAbsentOnThisGeneration, view.Refusal);
        Assert.Equal(WhisparrGeneration.V2, view.Generation);
        Assert.Empty(host.Client.Verbs);
    }

    // The narrowest reason, and the only one reachable once the two above are ruled out. Without
    // this case those two would pass with the third reason never answered at all.
    [Fact]
    public async Task NoMetadataLinkAloneAnswersTheMetadataLinkAtTheRoute()
    {
        await using var host = await MonitorHost.CreateAsync();
        var performerId = await host.SeedPerformerAsync(endpoint: null, remoteId: null);

        var view = await host.MonitorAsync("performer", performerId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    [Fact]
    public async Task NoReasonHoldingLeavesTheEntityMonitorable()
    {
        await using var host = await MonitorHost.CreateAsync();
        var performerId = await host.SeedPerformerAsync(
            MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);

        var view = await host.MonitorAsync("performer", performerId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.True(view.Monitored);
    }

    // No pure decider answers this kind. It is read off the transport, so reaching it means driving
    // an answer past the read bound through the client.
    private static async Task<MonitorRefusalKind> TheKindAnAnswerPastTheReadBoundProducesAsync()
    {
        var handler = BodyRecordingHandler.AnsweringPastTheReadBound();

        var answered = await TestWhisparrClient.Over(handler)
            .ReadHistoryAsync(1, 10, TestContext.Current.CancellationToken);

        return MonitoringProjector.Classify(answered).Refusal;
    }

    // The function stating this one is private to the API, so it is produced by a route reading an
    // entity the instance does not hold.
    private static async Task<MonitorRefusalKind> TheKindAnEntityTheInstanceDoesNotHoldProducesAsync()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        return (await host.AddAllMissingViewAsync("studio", studioId)).Refusal;
    }

    // Stated by the API too, and reachable only after a write left: the instance takes the add and
    // the read straight afterwards still finds nothing.
    private static async Task<MonitorRefusalKind> TheKindAReadBackThatFindsNothingProducesAsync()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync), MonitorHost.Json(404, string.Empty));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        return (await host.MonitorAsync(studioId)).Refusal;
    }

    // Answered by the one seam an add body's root is composed through, for an entity whose own
    // library root the instance agreed no spelling for.
    private static async Task<MonitorRefusalKind> TheKindAnEntityWhoseRootAgreedOnNothingProducesAsync()
        => (await EntityAddDefaults.ComposeAsync(
            new AddDefaults(4, "/config/library"),
            ["G:/Downloads/P"],
            (_, _) => Task.FromResult(1),
            (coveRoot, _) => Task.FromResult(
                new AddressedFolder(null, FolderAgreementRefusal.NothingResolved, coveRoot, [])),
            TestContext.Current.CancellationToken)).Refusal;

    // Each entry is the return of a real call rather than a named kind.
    private static IEnumerable<MonitorRefusalKind> Reachable()
    {
        foreach (var identity in new[]
                 {
                     MonitorRefusalKind.None,
                     MonitorRefusalKind.NoIdentityInThisNamespace,
                     MonitorRefusalKind.SeveralIdentitiesInThisNamespace,
                 })
        {
            yield return MonitoringProjector.FirstRefusal(new MonitoringProjector.MonitorReasons(
                NoConnectionConfigured: false,
                CapabilityAbsentOnThisGeneration: false,
                IdentityRefusal: identity));
        }

        yield return MonitoringProjector.FirstRefusal(new MonitoringProjector.MonitorReasons(
            NoConnectionConfigured: true,
            CapabilityAbsentOnThisGeneration: false,
            IdentityRefusal: MonitorRefusalKind.None));

        yield return MonitoringProjector.FirstRefusal(new MonitoringProjector.MonitorReasons(
            NoConnectionConfigured: false,
            CapabilityAbsentOnThisGeneration: true,
            IdentityRefusal: MonitorRefusalKind.None));

        yield return AddDefaultsProjector.From("[]", MonitorHost.OneRootFolder).Refusal;
        yield return AddDefaultsProjector.From(MonitorHost.UnsortedProfiles, "[]").Refusal;
        yield return MonitoringProjector.AcceptedStatus(409);
    }
}
