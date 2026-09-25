using System.Net;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Identity;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// Driven through the mapped routes rather than the port: a handler that read an identifier out of
// the request body would pass a test that called the port directly. Each identity refusal asserts
// an empty outbound log, so each is paired with a case that sends through the same double.
public sealed class EntityIdentityPortTests
{
    // The host rule reduces a host name to its last two labels, so this and the stored spelling
    // are one source.
    private const string SameSourceOtherSpelling = "https://www.stashdb.org/graphql";

    private const string OtherNamespace = "theporndb.net/graphql";

    // A second identifier in the stored namespace, so two rows disagree.
    private const string SecondIdentifier = "11111111-2222-3333-4444-555555555555";

    // Four different member names rather than one, because a handler binding any of them would
    // pass a single-name assertion. Each is a spelling one generation or the other uses.
    private const string BodyCarryingFourIdentifiers = """
        {
          "scope": "futureScenes",
          "foreignId": "00000000-0000-0000-0000-000000000000",
          "remoteId": "deadbeef-dead-beef-dead-beefdeadbeef",
          "studioId": 999,
          "tvdbId": 3372
        }
        """;

    // Transcribed by hand. A set read from the route table would agree with the route table
    // whatever it says.
    public static TheoryData<string> ActionVerbs => new("monitor", "unmonitor", "scope");

    // Asserts the seeded value rather than the absence of the body's values, which would also hold
    // for a path that sent nothing at all.
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

    // The control the emptiness assertions rest on: every action route does send through this
    // double when the entity carries one identity.
    [Theory]
    [MemberData(nameof(ActionVerbs))]
    public async Task EveryActionRouteSendsWhenTheEntityCarriesOneIdentity(string verb)
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrCore.SetStudioScopeAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrCore.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
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

    // The same refusal kind as no row at all: the namespace that counts is whichever the connected
    // instance identifies entities in.
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

    // The two spellings are one source under the host rule, so both rows match and which one would
    // be sent depends on row order.
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

    // The refusal above is about disagreement, not about row count.
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

    // The tag cases read the port rather than a route: no action route is mounted on a tag, so a
    // route-driven case would assert the missing route's answer instead of the resolution.
    [Fact]
    public async Task ATagWithNoIdentityRowIsUnmatched()
    {
        await using var host = await MonitorHost.CreateAsync();
        var tagId = await host.SeedTagAsync(endpoint: null, remoteId: null);

        var resolved = await host.Identities.ResolveAsync(
            WhisparrEntityKind.Tag, tagId, WhisparrGeneration.V3, TestContext.Current.CancellationToken);

        Assert.Equal(IdentityResolution.Unmatched, resolved);
    }

    [Fact]
    public async Task ATagCarryingOneIdentityResolvesToIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        var tagId = await host.SeedTagAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var resolved = await host.Identities.ResolveAsync(
            WhisparrEntityKind.Tag, tagId, WhisparrGeneration.V3, TestContext.Current.CancellationToken);

        Assert.Equal(IdentityResolution.At(MonitorHost.StudioRemoteIdValue), resolved);
    }

    [Fact]
    public async Task ATagCarryingTwoDisagreeingIdentitiesIsAmbiguous()
    {
        await using var host = await MonitorHost.CreateAsync();
        var tagId = await host.SeedTagAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        await host.AddTagIdentityAsync(tagId, SameSourceOtherSpelling, SecondIdentifier);

        var resolved = await host.Identities.ResolveAsync(
            WhisparrEntityKind.Tag, tagId, WhisparrGeneration.V3, TestContext.Current.CancellationToken);

        Assert.Equal(IdentityResolution.Ambiguous, resolved);
    }

    // Asserts the projection rather than the row count: a read that loaded every row and filtered
    // afterwards would be linear in the library and still pass a count assertion. Read off the
    // query's translated text through the same base context the port binds.
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

    // Asserted against the host's own same-source rule rather than string equality.
    [Fact]
    public void TheStoredSpellingAndThePreferredOneNameOneSource()
    {
        Assert.True(EndpointMatchGuard.SameSource(MonitorHost.StoredEndpoint, SameSourceOtherSpelling));
        Assert.False(EndpointMatchGuard.SameSource(MonitorHost.StoredEndpoint, OtherNamespace));
    }

    // Paired with the sending control above: without it a 403 could equally mean the route is
    // broken for every caller.
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

    [Theory]
    [MemberData(nameof(ActionVerbs))]
    public async Task EveryActionRouteRefusesAKindItCannotRead(string verb)
    {
        await using var host = await MonitorHost.CreateAsync();

        using var answered = await host.PostRawAsync("banana", 1, verb, ScopedBody);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    // The field a narrower scope is carried in exists on one resource only, so a scope named for
    // any other kind is a request the contract cannot express.
    [Fact]
    public async Task TheScopeRouteRefusesAPerformerAsAMalformedRequest()
    {
        await using var host = await MonitorHost.CreateAsync();
        var performerId = await host.SeedPerformerAsync(
            MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);

        using var answered = await host.PostRawAsync("performer", performerId, "scope", ScopedBody);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);

        // The same performer is reachable on the two routes it expresses, so the refusal above is
        // about the scope rather than about the kind being unreachable.
        Assert.Equal(
            MonitorRefusalKind.None,
            (await host.ActRawAsync("performer", performerId, "unmonitor", "{}")).Refusal);
    }

    [Fact]
    public async Task UnmonitoringAMonitoredStudioSendsTheFlagFalseOnce()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrCore.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
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

    // Widening a scope is not the same gesture as monitoring, so the flag answered is the one the
    // instance reports.
    [Fact]
    public async Task AScopeChangeSendsTheScopeAskedForAndLeavesTheFlagAsTheInstanceReportsIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrCore.SetStudioScopeAsync), MonitorHost.Json(200, "{}"));
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

    private static string ScopedBody => """{"scope":"futureScenes"}""";
}
