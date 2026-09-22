using System.Reflection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// The two studio-read documents are inputs captured from the instance, and every expected value is
// written out by hand: one computed from the projection under test would agree with it whatever
// either said. Nothing here reads the date gate's value, only whether the member is there.
public sealed class ScopeInForceTests
{
    private const string DateGateSetFixture = "whisparr-v3-3.3.8.1097-studio-read-after-date-set.json";
    private const string DateGateAbsentFixture =
        "whisparr-v3-3.3.8.1097-studio-read-after-date-absent.json";

    // A held studio body carrying the gate, in the spelling the read answered it in.
    private static string DateGateSet => ProbeFixtures.Read(DateGateSetFixture);

    // The same studio added without the gate, so the member is not there at all.
    private static string DateGateAbsent => ProbeFixtures.Read(DateGateAbsentFixture);

    // The spelling this library holds v2's identity rows under.
    private const string V2Endpoint = "theporndb.net/graphql";

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

    // A gate present but null is the same absence as no member at all: the instance's own help text
    // says that value is ignored.
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

    // What v2 answers a studio read with was never measured, so there is nothing to read even for a
    // body carrying v3's member.
    [Fact]
    public void V2ReportsNoScopeForAStudio()
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

    // Marking a whole back catalogue wanted spends indexer traffic and disk, and on v3 narrowing the
    // scope again does not undo it. So the wider scope is the one answer no absence of information
    // may produce.
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
            MonitorRefusalKind.NoIdentityInThisNamespace,
            GenerationCapabilities.AScopeChangeIsRetroactiveOn(WhisparrGeneration.V3)).Scope);
    }

    // Driven through the mounted route, because every other case here calls the projection. The
    // read-back body names a different scope from the request: a case where the two agree would pass
    // against a substitution as well. The host answers the entity read twice, as not held and then
    // as held and monitored, and the held answer carries no date gate.
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

    // v2 reports no scope for a studio at all, so null says the read carried none and is distinct
    // from every particular scope.
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

    // A defaulted parameter is how a call site that never decided the question comes to answer one.
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
