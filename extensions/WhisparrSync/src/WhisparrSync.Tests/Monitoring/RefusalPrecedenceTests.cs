using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Which single reason a user reads when more than one holds at once.
/// </summary>
/// <remarks>
/// The whole table is enumerated rather than sampled. More than one reason holding is ordinary: an
/// entity with no metadata link, on the older generation, with nothing configured has all three, and
/// the case nobody writes a test for is the one where the answer is decided by evaluation order
/// instead of by a decision.
/// <para>
/// The expected kind per row is written out by hand. A table computed from the function it checks
/// agrees with it whatever the order is.
/// </para>
/// </remarks>
public sealed class RefusalPrecedenceTests
{
    /// <summary>
    /// All eight combinations of the three reasons, each with the one kind it must answer.
    /// </summary>
    /// <remarks>
    /// The identity slot carries the narrowest identity kind, which is the one MON-3 names. The other
    /// identity kinds ride the same slot and are covered separately.
    /// </remarks>
    public static TheoryData<bool, bool, bool, MonitorRefusalKind> EveryCombination => new()
    {
        // no connection, generation gap, no metadata link -> the one kind
        { false, false, false, MonitorRefusalKind.None },
        { false, false, true, MonitorRefusalKind.NoIdentityInThisNamespace },
        { false, true, false, MonitorRefusalKind.CapabilityAbsentOnThisGeneration },
        { false, true, true, MonitorRefusalKind.CapabilityAbsentOnThisGeneration },
        { true, false, false, MonitorRefusalKind.NotConfigured },
        { true, false, true, MonitorRefusalKind.NotConfigured },
        { true, true, false, MonitorRefusalKind.NotConfigured },
        { true, true, true, MonitorRefusalKind.NotConfigured },
    };

    /// <summary>Every combination answers exactly one kind, and it is the transcribed one.</summary>
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

    /// <summary>
    /// Whichever identity kind the library produced rides the third slot unchanged.
    /// </summary>
    /// <remarks>
    /// The precedence chooses BETWEEN the three reasons and never narrows one of them. An identity
    /// kind rewritten on the way through would collapse two different sentences into one.
    /// </remarks>
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

    /// <summary>
    /// An earlier reason wins over EVERY identity kind, not only over the narrowest one.
    /// </summary>
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

    /// <summary>
    /// Every kind the enum declares is produced by some case this product can reach.
    /// </summary>
    /// <remarks>
    /// Iterates the enum rather than a literal list, so a kind added later fails here until something
    /// produces it. A kind nothing can produce is a dead value that a surface still has to carry a
    /// sentence for.
    /// <para>
    /// The producing set is built by CALLING the real deciders rather than by naming their outputs. A
    /// list of names would agree with itself after a decider stopped answering one of them.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryDeclaredRefusalKindIsProducedBySomeReachableCase()
    {
        var produced = new HashSet<MonitorRefusalKind>(Reachable());

        foreach (var kind in Enum.GetValues<MonitorRefusalKind>())
        {
            Assert.Contains(kind, produced);
        }
    }

    /// <summary>
    /// Three simultaneous reasons produce one kind on the wire, and it is the precedence's own.
    /// </summary>
    /// <remarks>
    /// Driven through the mapped route, so what is asserted is that the handler's own short-circuit
    /// order agrees with the stated precedence rather than that the pure function is self-consistent.
    /// A performer on the older generation with nothing configured and no identity row holds all
    /// three reasons at once.
    /// </remarks>
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

    /// <summary>
    /// A generation gap and no metadata link together answer the gap, at the route.
    /// </summary>
    /// <remarks>
    /// A connection IS configured here, so the pair under test is the second and third reasons. The
    /// older generation holds no performer role, so the gap is a real one rather than a set built
    /// holding nothing.
    /// </remarks>
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

    /// <summary>
    /// No metadata link alone answers the metadata link, at the route.
    /// </summary>
    /// <remarks>
    /// The narrowest reason, and the only one reachable once the two above are ruled out. Paired with
    /// the two cases above it, this is what makes those two about the ORDER rather than about the
    /// third reason never being answered at all.
    /// </remarks>
    [Fact]
    public async Task NoMetadataLinkAloneAnswersTheMetadataLinkAtTheRoute()
    {
        await using var host = await MonitorHost.CreateAsync();
        var performerId = await host.SeedPerformerAsync(endpoint: null, remoteId: null);

        var view = await host.MonitorAsync("performer", performerId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>No reason holding leaves the entity monitorable, at the route.</summary>
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

    /// <summary>Every kind some decider in this product actually answers with.</summary>
    /// <remarks>
    /// Each entry is the return of a real call. The three reasons come from the precedence, the two
    /// composition stops from the add-defaults decision, and the instance's own decline from the
    /// write classifier.
    /// </remarks>
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
