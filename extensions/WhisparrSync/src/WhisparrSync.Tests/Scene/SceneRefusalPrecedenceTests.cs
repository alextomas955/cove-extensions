using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Scene;

// The refusal order is fixed: no connection, then no identity or several identities, then an
// absent capability, then no entry, then not monitoring. Each case sets up the step under test
// together with every step beneath it, so a handler asking in another order answers a refusal
// further down and fails.
public sealed class SceneRefusalPrecedenceTests
{
    private const string SceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private const string OtherSceneId = "8b1f7d40-2a63-4f19-95cd-0e7a6b3c2d15";

    private const string Search = "search";

    // The source v2 identifies against, which v3 reads as another namespace.
    private const string OtherEndpoint = "theporndb.net/graphql";

    [Fact]
    public async Task NoInstanceConnectedOutranksEveryOtherStep()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);
        var coveId = await SeedSceneAsync(host, endpoint: null, remoteId: null);
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(200, "[]"));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.NoInstanceConnected, result.Refusal);
        Assert.Empty(host.Client.SceneStatuses);
    }

    [Fact]
    public async Task NoIdentityOutranksAnAbsentCapability()
    {
        await using var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2);
        var coveId = await SeedSceneAsync(host, endpoint: null, remoteId: null);

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.NoIdentityInThisNamespace, result.Refusal);
        Assert.Empty(host.Client.SceneStatuses);
    }

    // Conflicting links and no link at all send a reader to different places, so the two carry
    // different refusals. The third row is in another namespace and takes no part in the conflict.
    [Fact]
    public async Task SeveralConflictingLinksAnswerTheirOwnRefusal()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, MonitorHost.StoredEndpoint, SceneId);
        await host.AddSceneIdentityAsync(coveId, MonitorHost.StoredEndpoint, OtherSceneId);
        await host.AddSceneIdentityAsync(coveId, OtherEndpoint, OtherSceneId);

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.SeveralIdentitiesInThisNamespace, result.Refusal);
        Assert.Empty(host.Client.SceneStatuses);
    }

    // Two rows naming the same scene are one link, not a conflict, so the identity step passes and
    // the refusal comes from a step below it.
    [Fact]
    public async Task TwoRowsNamingTheSameSceneAreNotAConflict()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, MonitorHost.StoredEndpoint, SceneId);
        await host.AddSceneIdentityAsync(coveId, MonitorHost.StoredEndpoint, SceneId);
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(200, "[]"));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.NotEqual(SceneRefusalKind.SeveralIdentitiesInThisNamespace, result.Refusal);
        Assert.NotEqual(SceneRefusalKind.NoIdentityInThisNamespace, result.Refusal);
    }

    // Whisparr v2 keeps no scene records, so it registers neither the read nor the grab. The scene
    // carries an identity in that generation's namespace, so the step above does not apply.
    [Fact]
    public async Task AnAbsentCapabilityOutranksNoEntry()
    {
        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var coveId = await SeedSceneAsync(host, OtherEndpoint, SceneId);

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.CapabilityAbsentOnThisGeneration, result.Refusal);
        Assert.Empty(host.Client.SceneStatuses);
        Assert.Empty(host.Client.Acting);
    }

    // An instance holding no entry holds no flag either, so a handler reading the flag first
    // answers not monitoring for a scene the instance never named.
    [Fact]
    public async Task NoEntryOutranksNotMonitoring()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, MonitorHost.StoredEndpoint, SceneId);
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(200, "[]"));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.WhisparrHasNoEntryForScene, result.Refusal);
        Assert.Empty(host.Client.Acting);
    }

    [Fact]
    public async Task NotMonitoringIsTheLastStepAndStillSendsNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, MonitorHost.StoredEndpoint, SceneId);
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(200, """[{"id":812,"monitored":false}]"""));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.WhisparrIsNotMonitoringThisScene, result.Refusal);
        Assert.Single(host.Client.SceneStatuses);
        Assert.Empty(host.Client.Acting);
    }

    private static async Task<int> SeedSceneAsync(
        MonitorHost host, string? endpoint, string? remoteId)
    {
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        return await host.SeedStudioSceneAsync(studioId, endpoint, remoteId);
    }
}
