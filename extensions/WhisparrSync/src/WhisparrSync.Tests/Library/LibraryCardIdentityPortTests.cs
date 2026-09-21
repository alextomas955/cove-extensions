using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Library;

public sealed class LibraryCardIdentityPortTests
{
    // A different spelling from the one MonitorHost.StoredEndpoint stores, on purpose. The two
    // name one source under the host's own rule.
    private const string StandardStashDbAddress = "https://stashdb.org/graphql";

    // A spelling belonging to the other generation's namespace.
    private const string OtherNamespaceEndpoint = "theporndb.net/graphql";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AVideoCarryingOneMatchingRowIsKnownByIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), MonitorHost.StoredEndpoint, FirstScene);

        Assert.Equal(
            [new LibraryCardIdentity(videoId, FirstScene)],
            await ResolveAsync(host, videoId));
    }

    // The host's own same-source rule decides this. Comparing the endpoint spellings as strings
    // would leave a card silent for a reason that is not about the connected instance.
    [Fact]
    public async Task ASpellingOfTheSameSourceIsAnsweredRatherThanComparedAsAString()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), StandardStashDbAddress, FirstScene);

        Assert.Equal(
            [new LibraryCardIdentity(videoId, FirstScene)],
            await ResolveAsync(host, videoId));
    }

    [Fact]
    public async Task AVideoWhoseRowsNameAnotherSourceIsKnownByNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), OtherNamespaceEndpoint, FirstScene);

        Assert.Empty(await ResolveAsync(host, videoId));
    }

    [Fact]
    public async Task AVideoCarryingNoRowIsKnownByNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), null, null);

        Assert.Empty(await ResolveAsync(host, videoId));
    }

    // Both rows match the source, so a read picking the first would take whichever the database
    // returned, and a card would report a status about a scene nobody chose.
    [Fact]
    public async Task AVideoNamingTwoDifferentScenesInOneNamespaceIsKnownByNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), MonitorHost.StoredEndpoint, FirstScene);
        await host.AddSceneIdentityAsync(videoId, StandardStashDbAddress, SecondScene);

        Assert.Empty(await ResolveAsync(host, videoId));
    }

    // A read that materialized the library's own identity rows would answer the same identifiers
    // and grow with the library.
    [Fact]
    public async Task OnlyTheRequestedVideosAreAnsweredForAndInTheRequestedOrder()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var first = await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        var second = await host.SeedStudioSceneAsync(
            studioId, MonitorHost.StoredEndpoint, SecondScene);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, "unasked-for");

        Assert.Equal(
            [new LibraryCardIdentity(second, SecondScene), new LibraryCardIdentity(first, FirstScene)],
            await ResolveAsync(host, second, first));
    }

    // A page read drops a contested video, but a caller acting on one scene owes the reader the
    // reason, so the two absences are answered apart here.
    [Fact]
    public async Task OneSceneNamingTwoDifferentScenesAnswersTheConflictRatherThanAnAbsence()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), MonitorHost.StoredEndpoint, FirstScene);
        await host.AddSceneIdentityAsync(videoId, StandardStashDbAddress, SecondScene);

        Assert.Equal(SceneCardIdentity.Ambiguous, await ResolveOneAsync(host, videoId));
    }

    [Fact]
    public async Task OneSceneNamedByNothingAnswersTheAbsence()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), null, null);

        Assert.Equal(SceneCardIdentity.Unmatched, await ResolveOneAsync(host, videoId));
    }

    // A row in the other generation's namespace is not part of this one's answer, so it neither
    // names the scene nor contests it.
    [Fact]
    public async Task ARowInAnotherNamespaceNeitherNamesTheSceneNorContestsIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), MonitorHost.StoredEndpoint, FirstScene);
        await host.AddSceneIdentityAsync(videoId, OtherNamespaceEndpoint, SecondScene);

        Assert.Equal(SceneCardIdentity.At(FirstScene), await ResolveOneAsync(host, videoId));
    }

    // Two rows carrying one identifier agree, so the scene is named rather than contested.
    [Fact]
    public async Task TwoRowsCarryingOneIdentifierNameTheScene()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), MonitorHost.StoredEndpoint, FirstScene);
        await host.AddSceneIdentityAsync(videoId, StandardStashDbAddress, FirstScene);

        Assert.Equal(SceneCardIdentity.At(FirstScene), await ResolveOneAsync(host, videoId));
    }

    private static async Task<IReadOnlyList<LibraryCardIdentity>> ResolveAsync(
        MonitorHost host, params int[] coveIds)
        => await host.CardIdentities.ResolveAsync(coveIds, WhisparrGeneration.V3, TestCt);

    private static async Task<SceneCardIdentity> ResolveOneAsync(MonitorHost host, int coveId)
        => await host.CardIdentities.ResolveOneAsync(coveId, WhisparrGeneration.V3, TestCt);
}
