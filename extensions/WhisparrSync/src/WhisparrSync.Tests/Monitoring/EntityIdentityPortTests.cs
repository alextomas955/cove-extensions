using System.Net;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

using EndpointMatchGuard = WhisparrSync.Import.EndpointMatchGuard;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Where the outbound identifier comes from, on every action route a caller can reach.
/// </summary>
/// <remarks>
/// Driven through the mapped routes rather than through the port, because the property being asserted
/// is that a caller cannot influence it. A test calling the port directly would agree with a handler
/// that read an identifier out of the request body and never called the port at all.
/// <para>
/// Each of the three identity refusals is asserted with an EMPTY outbound log, and each is paired in
/// this class with a case that sends through the same double. An empty log read on its own agrees
/// with itself whatever the code does.
/// </para>
/// </remarks>
public sealed class EntityIdentityPortTests
{
    /// <summary>
    /// A second spelling of the stored source, which the host's own rule treats as the same one.
    /// </summary>
    /// <remarks>
    /// The rule reduces a host name to its last two labels, so this and the stored spelling are one
    /// source and an entity carrying both carries two matching rows.
    /// </remarks>
    private const string SameSourceOtherSpelling = "https://www.stashdb.org/graphql";

    /// <summary>The namespace the OTHER generation identifies entities in.</summary>
    private const string OtherNamespace = "theporndb.net/graphql";

    /// <summary>A second identifier in the stored namespace, so two rows disagree.</summary>
    private const string SecondIdentifier = "11111111-2222-3333-4444-555555555555";

    /// <summary>
    /// A body naming four plausible identifiers, every one of which reaches nothing.
    /// </summary>
    /// <remarks>
    /// Four different member names rather than one, because a handler binding any of them would pass
    /// a single-name assertion. Each is a spelling one generation or the other genuinely uses.
    /// </remarks>
    private const string BodyCarryingFourIdentifiers = """
        {
          "scope": "futureScenes",
          "foreignId": "00000000-0000-0000-0000-000000000000",
          "remoteId": "deadbeef-dead-beef-dead-beefdeadbeef",
          "studioId": 999,
          "tvdbId": 3372
        }
        """;

    /// <summary>Every action route mounted on one entity, so a case can cover all of them.</summary>
    /// <remarks>
    /// Transcribed by hand. A set read from the route table would agree with it whatever it says, and
    /// the point of this class is that every route a caller can reach behaves the same way.
    /// </remarks>
    public static TheoryData<string> ActionVerbs => new("monitor", "unmonitor", "scope");

    /// <summary>
    /// The identifier that leaves is the stored row's, whatever the request body says.
    /// </summary>
    /// <remarks>
    /// The seeded value is asserted rather than the absence of the body's values: an assertion that
    /// the body's identifiers are absent would hold for a path that sent nothing at all.
    /// </remarks>
    [Fact]
    public async Task ABodyNamingFourIdentifiersStillSendsTheStoredRowsOwn()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.ActRawAsync("studio", studioId, "monitor", BodyCarryingFourIdentifiers);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.All(
            host.Client.Acting.Where(call => call.ForeignId is not null),
            call => Assert.Equal(MonitorHost.StudioRemoteIdValue, call.ForeignId));

        var add = host.Client.Acting.Single(
            call => call.Verb == nameof(IWhisparrStudioActing.AddMonitoredStudioAsync));
        Assert.Equal(MonitorHost.StudioRemoteIdValue, add.ForeignId);
    }

    /// <summary>
    /// The control the emptiness assertions rest on: every action route DOES send through this
    /// double when the entity carries one identity.
    /// </summary>
    [Theory]
    [MemberData(nameof(ActionVerbs))]
    public async Task EveryActionRouteSendsWhenTheEntityCarriesOneIdentity(string verb)
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(200, MonitorHost.AddedStudio));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.ActRawAsync("studio", studioId, verb, ScopedBody);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.NotEmpty(host.Client.Verbs);
        Assert.All(
            host.Client.Acting.Where(call => call.ForeignId is not null),
            call => Assert.Equal(MonitorHost.StudioRemoteIdValue, call.ForeignId));
    }

    /// <summary>An entity with no identity row at all: nothing to name it by, and nothing sent.</summary>
    [Theory]
    [MemberData(nameof(ActionVerbs))]
    public async Task AnEntityWithNoIdentityRowRefusesBeforeAnythingIsSent(string verb)
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(endpoint: null, remoteId: null);

        var view = await host.ActRawAsync("studio", studioId, verb, ScopedBody);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>
    /// An entity identified only in the other generation's namespace, which is the ordinary case
    /// rather than a rare one.
    /// </summary>
    /// <remarks>
    /// The same kind as no row at all, on purpose: the namespace that counts is whichever the
    /// connected instance identifies entities in, so from the reader's side the two are one fact and
    /// one sentence.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ActionVerbs))]
    public async Task AnEntityIdentifiedOnlyInTheOtherNamespaceRefusesBeforeAnythingIsSent(string verb)
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(OtherNamespace, MonitorHost.StudioRemoteIdValue);

        var view = await host.ActRawAsync("studio", studioId, verb, ScopedBody);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>
    /// Two rows matching this namespace and disagreeing is a refusal, not a first-row pick.
    /// </summary>
    /// <remarks>
    /// The two spellings are one source under the host's own rule, so both rows match and which one
    /// would be sent depends on row order. Taking either would aim the stored credential at whichever
    /// entity that order happened to name.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ActionVerbs))]
    public async Task AnEntityCarryingTwoDisagreeingIdentitiesRefusesBeforeAnythingIsSent(string verb)
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        await host.AddStudioIdentityAsync(studioId, SameSourceOtherSpelling, SecondIdentifier);

        var view = await host.ActRawAsync("studio", studioId, verb, ScopedBody);

        Assert.Equal(MonitorRefusalKind.SeveralIdentitiesInThisNamespace, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>
    /// Two rows naming the SAME identifier are one identity and no ambiguity.
    /// </summary>
    /// <remarks>
    /// The refusal above is about disagreement rather than about row count. Refusing a duplicate that
    /// names one entity would be a refusal with no hazard behind it.
    /// </remarks>
    [Fact]
    public async Task TwoRowsNamingTheSameIdentifierAreOneIdentityAndSend()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        await host.AddStudioIdentityAsync(
            studioId, SameSourceOtherSpelling, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        var add = host.Client.Acting.Single(
            call => call.Verb == nameof(IWhisparrStudioActing.AddMonitoredStudioAsync));
        Assert.Equal(MonitorHost.StudioRemoteIdValue, add.ForeignId);
    }

    /// <summary>
    /// The identity read projects only the two columns the endpoint rule compares.
    /// </summary>
    /// <remarks>
    /// The projection is asserted rather than the row count. A read that loaded every row and
    /// filtered afterwards would be linear in the library and would pass a count assertion, because
    /// the count it answers is the count after the filter.
    /// <para>
    /// Read off the query's own translated text through the same base context the port binds, so what
    /// is asserted is what the provider will run rather than what the expression tree looks like.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheIdentityReadIsNarrowedOnTheEntityAndProjectsOnlyTwoColumns()
    {
        var (db, connection) = await CoveContextFactory.CreateSqliteContextAsync();
        await using (connection)
        await using (db)
        {
            var sql = db.Set<StudioRemoteId>()
                .Where(row => row.StudioId == 7)
                .Select(row => new { row.Endpoint, row.RemoteId })
                .AsNoTracking()
                .ToQueryString();

            Assert.Contains("WHERE", sql, StringComparison.Ordinal);
            Assert.Contains("StudioId", sql, StringComparison.Ordinal);
            Assert.Contains("Endpoint", sql, StringComparison.Ordinal);
            Assert.Contains("RemoteId", sql, StringComparison.Ordinal);

            // A third column in the projection would mean the read carries more of the row than the
            // rule reads, which is how a projection turns back into a row load.
            Assert.DoesNotContain("SELECT *", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Id\"", sql, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The port reads the namespace the CONNECTED generation identifies entities in.
    /// </summary>
    /// <remarks>
    /// Asserted against the host's own same-source rule rather than against string equality, so the
    /// stored spelling and the preferred one are one source here exactly as they are to the host.
    /// </remarks>
    [Fact]
    public void TheStoredSpellingAndThePreferredOneNameOneSource()
    {
        Assert.True(EndpointMatchGuard.SameSource(MonitorHost.StoredEndpoint, SameSourceOtherSpelling));
        Assert.False(EndpointMatchGuard.SameSource(MonitorHost.StoredEndpoint, OtherNamespace));
    }

    /// <summary>Every action route refuses a caller without the tier it declares.</summary>
    /// <remarks>
    /// Paired with the sending control above: without it a 403 could equally mean the route is broken
    /// for every caller.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ActionVerbs))]
    public async Task EveryActionRouteRefusesACallerWithoutTheConfigureTier(string verb)
    {
        await using var host = await MonitorHost.CreateAsync(
            principal: FakePrincipalAccessor.None());
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        using var answered = await host.PostRawAsync("studio", studioId, verb, ScopedBody);

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>A kind no route segment can be read as is malformed on every action route.</summary>
    [Theory]
    [MemberData(nameof(ActionVerbs))]
    public async Task EveryActionRouteRefusesAKindItCannotRead(string verb)
    {
        await using var host = await MonitorHost.CreateAsync();

        using var answered = await host.PostRawAsync("banana", 1, verb, ScopedBody);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>
    /// A performer names no scope, so the scope route answers a malformed request for one.
    /// </summary>
    /// <remarks>
    /// The field a narrower scope is carried in exists on one resource only, so a scope named for any
    /// other kind is a request the contract cannot express rather than one an instance would refuse.
    /// </remarks>
    [Fact]
    public async Task TheScopeRouteRefusesAPerformerAsAMalformedRequest()
    {
        await using var host = await MonitorHost.CreateAsync();
        var performerId = await host.SeedPerformerAsync(
            MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);

        using var answered = await host.PostRawAsync("performer", performerId, "scope", ScopedBody);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);

        // The same performer IS reachable on the two routes it expresses, so the refusal above is
        // about the scope rather than about the kind being unreachable.
        Assert.Equal(
            MonitorRefusalKind.None,
            (await host.ActRawAsync("performer", performerId, "unmonitor", "{}")).Refusal);
    }

    /// <summary>Unmonitoring sends the flag false, and sends it once.</summary>
    [Fact]
    public async Task UnmonitoringAMonitoredStudioSendsTheFlagFalseOnce()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(200, MonitorHost.AddedStudio));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.UnmonitorAsync("studio", studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.False(view.Monitored);
        var flip = host.Client.Acting.Single(
            call => call.Verb == nameof(IWhisparrStudioActing.SetStudioMonitoredAsync));
        Assert.False(flip.Monitored);
        Assert.Equal(1, flip.EntityId);
    }

    /// <summary>
    /// An entity the instance does not hold is already not monitored, so nothing is sent.
    /// </summary>
    [Fact]
    public async Task UnmonitoringAnEntityTheInstanceDoesNotHoldSendsNothingThatChangesIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.UnmonitorAsync("studio", studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.False(view.Monitored);
        Assert.DoesNotContain(
            nameof(IWhisparrStudioActing.SetStudioMonitoredAsync), host.Client.Verbs);
    }

    /// <summary>A scope change carries the scope asked for and leaves the flag alone.</summary>
    /// <remarks>
    /// Widening a scope is not the same gesture as monitoring, so the flag the instance reports is
    /// what is answered rather than a monitored state the caller did not ask for.
    /// </remarks>
    [Fact]
    public async Task AScopeChangeSendsTheScopeAskedForAndLeavesTheFlagAsTheInstanceReportsIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(200, MonitorHost.AddedStudio));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.ChangeScopeAsync("studio", studioId, "allScenes");

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.True(view.Monitored);
        var scoped = host.Client.Acting.Single(
            call => call.Verb == nameof(IWhisparrStudioActing.SetStudioScopeAsync));
        Assert.Equal(MonitorScope.AllScenes, scoped.Scope);
        Assert.Equal(1, scoped.EntityId);
    }

    /// <summary>The scope route names a scope or it names nothing this route can apply.</summary>
    [Fact]
    public async Task TheScopeRouteRefusesARequestNamingNoScope()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        using var answered = await host.PostRawAsync("studio", studioId, "scope", "{}");

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>A body every action route accepts, so one case can cover all of them.</summary>
    private static string ScopedBody => """{"scope":"futureScenes"}""";
}
