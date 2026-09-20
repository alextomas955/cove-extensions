using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Library;

public sealed class LibrarySceneIdentityPortTests
{
    // A different spelling from the stored one, naming the same source.
    private const string StandardStashDbAddress = "https://stashdb.org/graphql";

    // A spelling belonging to the other generation's namespace.
    private const string OtherNamespaceEndpoint = "theporndb.net/graphql";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";
    private const string ThirdScene = "7b1e4d90-2c3a-4f81-95d6-0a8b7c6e5f43";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryIdentifiedSceneInTheLibraryIsAnsweredWhicheverEntityItSitsUnder()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedPerformerSceneAsync(
            await host.SeedPerformerAsync(null, null), MonitorHost.StoredEndpoint, SecondScene);

        Assert.Equal([FirstScene, SecondScene], await IdentitiesAsync(host));
    }

    [Fact]
    public async Task ASpellingOfTheSameSourceIsAnsweredRatherThanComparedAsAString()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), StandardStashDbAddress, FirstScene);

        Assert.Equal([FirstScene], await IdentitiesAsync(host));
    }

    [Fact]
    public async Task ASceneIdentifiedOnlyInTheOtherNamespaceIsNotAnswered()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), OtherNamespaceEndpoint, FirstScene);

        Assert.Empty(await IdentitiesAsync(host));
    }

    [Fact]
    public async Task ASceneCarryingNoRowIsCountedAsUnidentified()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedStudioSceneAsync(studioId, null, null);

        Assert.Equal(1, await UnidentifiedAsync(host));
    }

    // The identifier it carries is not one the connected instance names entries by, so it is as
    // unregistrable as a scene carrying none.
    [Fact]
    public async Task ASceneIdentifiedOnlyInTheOtherNamespaceIsCountedAsUnidentified()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), OtherNamespaceEndpoint, FirstScene);

        Assert.Equal(1, await UnidentifiedAsync(host));
    }

    // The pair is two rows in the database and one identified scene under the host's own rule. A
    // count that read the rows would report fewer unidentified scenes than there are.
    [Fact]
    public async Task ASceneCarryingTwoSpellingsOfOneSourceIsCountedIdentifiedOnce()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var videoId = await host.SeedStudioSceneAsync(
            studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.AddSceneIdentityAsync(videoId, StandardStashDbAddress, FirstScene);
        await host.SeedStudioSceneAsync(studioId, null, null);

        Assert.Equal(1, await UnidentifiedAsync(host));
    }

    // The count and the stream are separate members and a reader adds their answers together, so
    // both are checked on the same library.
    [Fact]
    public async Task ALibraryWithNothingIdentifiedAnswersNoIdentifierAndCountsEveryScene()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, null, null);
        await host.SeedStudioSceneAsync(studioId, null, null);

        Assert.Empty(await IdentitiesAsync(host));
        Assert.Equal(2, await UnidentifiedAsync(host));
    }

    [Fact]
    public async Task SeveralIdentifiedScenesUnderOneEntityAreAllAnswered()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, SecondScene);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, ThirdScene);

        Assert.Equal([FirstScene, SecondScene, ThirdScene], await IdentitiesAsync(host));
    }

    private static async Task<List<string>> IdentitiesAsync(MonitorHost host)
    {
        var answered = new List<string>();
        await foreach (var identity in host.LibraryScenes
            .SceneIdentities(WhisparrGeneration.V3, TestCt)
            .WithCancellation(TestCt))
        {
            answered.Add(identity);
        }

        answered.Sort(StringComparer.Ordinal);
        return answered;
    }

    private static Task<int> UnidentifiedAsync(MonitorHost host)
        => host.LibraryScenes.CountUnidentifiedAsync(WhisparrGeneration.V3, TestCt);
}
