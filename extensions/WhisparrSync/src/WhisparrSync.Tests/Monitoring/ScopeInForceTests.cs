using System.Reflection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// What the product is willing to say about the scope an entity is monitored at, and what it
/// refuses to say.
/// </summary>
/// <remarks>
/// The two studio-read documents are INPUTS captured from the instance. Every expected value is
/// written out by hand, because one computed from the projection under test would agree with it
/// whatever either said.
/// <para>
/// Nothing here reads the date gate's value. The shipped rule reads whether the member is there and
/// the transcription of that value lives in one pin; a second reader of it here would be this
/// product forming an opinion about a date it has none about.
/// </para>
/// </remarks>
public sealed class ScopeInForceTests
{
    private const string DateGateSetFixture = "whisparr-v3-3.3.8.1097-studio-read-after-date-set.json";
    private const string DateGateAbsentFixture =
        "whisparr-v3-3.3.8.1097-studio-read-after-date-absent.json";

    /// <summary>A held studio body carrying the gate, in the spelling the read answered it in.</summary>
    private static string DateGateSet => ProbeFixtures.Read(DateGateSetFixture);

    /// <summary>The same studio, added without the gate, so the member is not there at all.</summary>
    private static string DateGateAbsent => ProbeFixtures.Read(DateGateAbsentFixture);

    /// <summary>The spelling this library holds the older generation's identity rows under.</summary>
    private const string V2Endpoint = "theporndb.net/graphql";

    /// <summary>Bodies that report nothing at all about a scope.</summary>
    public static TheoryData<string?> BodiesThatReportNothing
    {
        get
        {
            TheoryData<string?> bodies = [];
            foreach (var body in new string?[]
                     { null, "", "   ", "not json at all", "[]", "\"a string\"", "123" })
            {
                bodies.Add(body);
            }

            return bodies;
        }
    }

    [Fact]
    public void AHeldStudioCarryingTheDateGateIsMonitoredAtTheNarrowerScope()
    {
        Assert.Equal(
            MonitorScope.FutureScenes,
            MonitoringProjector.ScopeIn(
                WhisparrEntityKind.Studio, WhisparrGeneration.V3, monitored: true, DateGateSet));
    }

    [Fact]
    public void AHeldStudioCarryingNoDateGateIsMonitoredAtTheWiderScope()
    {
        Assert.Equal(
            MonitorScope.AllScenes,
            MonitoringProjector.ScopeIn(
                WhisparrEntityKind.Studio, WhisparrGeneration.V3, monitored: true, DateGateAbsent));
    }

    /// <summary>
    /// A gate present but null is the same absence as no member at all, because it is a value the
    /// instance's own help text says is ignored.
    /// </summary>
    [Fact]
    public void ADateGateExplicitlySetToNothingReadsAsTheWiderScope()
    {
        Assert.Equal(
            MonitorScope.AllScenes,
            MonitoringProjector.ScopeIn(
                WhisparrEntityKind.Studio,
                WhisparrGeneration.V3,
                monitored: true,
                """{"id":1,"monitored":true,"afterDate":null}"""));
    }

    [Fact]
    public void APerformerReportsNoScopeOnEitherGeneration()
    {
        foreach (var generation in new[] { WhisparrGeneration.V3, WhisparrGeneration.V2 })
        {
            Assert.Null(MonitoringProjector.ScopeIn(
                WhisparrEntityKind.Performer, generation, monitored: true, DateGateSet));
            Assert.Null(MonitoringProjector.ScopeIn(
                WhisparrEntityKind.Performer, generation, monitored: true, DateGateAbsent));
        }
    }

    /// <summary>
    /// The older generation reports no scope even for a body carrying the newer one's member: what
    /// that generation answers a read with was never measured, so there is nothing to read.
    /// </summary>
    [Fact]
    public void TheOlderGenerationReportsNoScopeForAStudio()
    {
        Assert.Null(MonitoringProjector.ScopeIn(
            WhisparrEntityKind.Studio, WhisparrGeneration.V2, monitored: true, DateGateSet));
        Assert.Null(MonitoringProjector.ScopeIn(
            WhisparrEntityKind.Studio, WhisparrGeneration.V2, monitored: true, DateGateAbsent));
    }

    [Fact]
    public void AnUnmonitoredStudioReportsNoScopeWhateverItsBodyCarries()
    {
        Assert.Null(MonitoringProjector.ScopeIn(
            WhisparrEntityKind.Studio, WhisparrGeneration.V3, monitored: false, DateGateSet));
        Assert.Null(MonitoringProjector.ScopeIn(
            WhisparrEntityKind.Studio, WhisparrGeneration.V3, monitored: false, DateGateAbsent));
    }

    [Theory]
    [MemberData(nameof(BodiesThatReportNothing))]
    public void ABodyThatReportsNothingIsAnsweredWithNoScope(string? body)
    {
        Assert.Null(MonitoringProjector.ScopeIn(
            WhisparrEntityKind.Studio, WhisparrGeneration.V3, monitored: true, body));
    }

    /// <summary>
    /// The wider scope is answered only for a body that positively reported it, and never as the
    /// answer to a question the product could not read.
    /// </summary>
    /// <remarks>
    /// Marking a whole back catalogue wanted spends indexer traffic and disk, and on the newer
    /// generation narrowing the scope again does not undo it. So the expensive scope is the one
    /// answer no absence of information may produce.
    /// </remarks>
    [Fact]
    public void NothingTheProductCouldNotReadIsAnsweredWithTheWiderScope()
    {
        var unreadable = new (WhisparrEntityKind Kind, WhisparrGeneration Generation, bool Monitored, string? Body)[]
        {
            (WhisparrEntityKind.Studio, WhisparrGeneration.V3, true, null),
            (WhisparrEntityKind.Studio, WhisparrGeneration.V3, true, ""),
            (WhisparrEntityKind.Studio, WhisparrGeneration.V3, true, "not json at all"),
            (WhisparrEntityKind.Studio, WhisparrGeneration.V3, true, "[]"),
            (WhisparrEntityKind.Studio, WhisparrGeneration.V2, true, DateGateAbsent),
            (WhisparrEntityKind.Performer, WhisparrGeneration.V3, true, DateGateAbsent),
            (WhisparrEntityKind.Studio, WhisparrGeneration.V3, false, DateGateAbsent),
        };

        foreach (var (kind, generation, monitored, body) in unreadable)
        {
            Assert.NotEqual(
                MonitorScope.AllScenes,
                MonitoringProjector.ScopeIn(kind, generation, monitored, body));
        }

        // Paired with a positive, so an assertion that could never see the wider scope is not the
        // only thing this case reports.
        Assert.Equal(
            MonitorScope.AllScenes,
            MonitoringProjector.ScopeIn(
                WhisparrEntityKind.Studio, WhisparrGeneration.V3, monitored: true, DateGateAbsent));
    }

    [Fact]
    public void ARefusedAndANotConfiguredReadBothReportNoScope()
    {
        Assert.Null(EntityMonitoringView.NotConfigured(WhisparrEntityKind.Studio).Scope);
        Assert.Null(EntityMonitoringView.Refused(
            WhisparrEntityKind.Studio,
            WhisparrGeneration.V3,
            [],
            MonitorRefusalKind.NoIdentityInThisNamespace).Scope);
    }

    /// <summary>
    /// An acting route answers the scope its own read-back reports, not the one the request asked
    /// for.
    /// </summary>
    /// <remarks>
    /// Driven through the mounted route, because every other case here calls the projection and so
    /// asserts nothing about what an acting path answers. The read-back body deliberately names a
    /// DIFFERENT scope from the request: a case where the two agree passes against the substitution
    /// as well and reports nothing.
    /// <para>
    /// The host answers the entity read twice, as not held and then as held and monitored, and the
    /// held answer carries no date gate. So the request asks for the narrower scope and the read
    /// reports the wider one, which is the disagreement. Both expected values are literals.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnActingRoutesAnswerCarriesTheScopeItsOwnReadReports()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorRawAsync(studioId, """{"scope":"futureScenes"}""");

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.True(view.Monitored);
        Assert.Equal(MonitorScope.AllScenes, view.Scope);
    }

    /// <summary>
    /// An acting route answers no scope where its own read named none, rather than the one the
    /// request asked for.
    /// </summary>
    /// <remarks>
    /// The older generation reports no scope for a studio at all, so this is the reading a
    /// substitution most obviously breaks: null says the read carried none, and it is distinct from
    /// every particular scope.
    /// </remarks>
    [Fact]
    public async Task AnActingRoutesAnswerCarriesNoScopeWhereItsReadNamedNone()
    {
        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var studioId = await host.SeedStudioAsync(V2Endpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorRawAsync(studioId, """{"scope":"futureScenes"}""");

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.True(view.Monitored);
        Assert.Null(view.Scope);
    }

    /// <summary>
    /// No factory hands out a scope nobody chose. A defaulted parameter is how a call site that
    /// never decided the question comes to answer one.
    /// </summary>
    [Fact]
    public void NoFactoryParameterCarryingAScopeHasADefault()
    {
        var scopeParameters = typeof(EntityMonitoringView)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(member => member.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(MonitorScope?))
            .ToArray();

        Assert.NotEmpty(scopeParameters);
        Assert.All(scopeParameters, parameter => Assert.False(parameter.HasDefaultValue));
    }
}
