using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// Which identifiers the whole library is known by, and how many of its scenes carry none, read from
/// a real relational library.
/// </summary>
/// <remarks>
/// The namespace rule is the subject as much as the identifiers are. A row written under another
/// spelling of the same source names an identified scene, and a read comparing endpoint spellings as
/// strings would count that scene among the ones Whisparr cannot be told about.
/// <para>
/// The two members have to agree about what an identified scene is. A count derived independently of
/// the stream would answer a different number from the stream a run then walks.
/// </para>
/// </remarks>
public sealed class LibrarySceneIdentityPortTests
{
    /// <summary>A different spelling from the stored one, naming the same source.</summary>
    private const string StandardStashDbAddress = "https://stashdb.org/graphql";

    /// <summary>A spelling belonging to the OTHER generation's namespace.</summary>
    private const string OtherNamespaceEndpoint = "theporndb.net/graphql";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";
    private const string ThirdScene = "7b1e4d90-2c3a-4f81-95d6-0a8b7c6e5f43";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>Every identified scene is answered, whichever entity it sits under.</summary>
    /// <remarks>
    /// The whole library and not one entity's part of it, which is what makes this read the sync's
    /// own rather than the catalogue surface's.
    /// </remarks>
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

    /// <summary>A row under a different spelling of the same source IS answered.</summary>
    [Fact]
    public async Task ASpellingOfTheSameSourceIsAnsweredRatherThanComparedAsAString()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), StandardStashDbAddress, FirstScene);

        Assert.Equal([FirstScene], await IdentitiesAsync(host));
    }

    /// <summary>A row naming the other generation's source is answered by nothing.</summary>
    [Fact]
    public async Task ASceneIdentifiedOnlyInTheOtherNamespaceIsNotAnswered()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), OtherNamespaceEndpoint, FirstScene);

        Assert.Empty(await IdentitiesAsync(host));
    }

    /// <summary>A scene carrying no identity row at all is counted as one that cannot be told about.</summary>
    [Fact]
    public async Task ASceneCarryingNoRowIsCountedAsUnidentified()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedStudioSceneAsync(studioId, null, null);

        Assert.Equal(1, await UnidentifiedAsync(host));
    }

    /// <summary>
    /// A scene identified only in the other namespace is counted as unidentified too.
    /// </summary>
    /// <remarks>
    /// It carries an identifier, and not one the connected instance names entries by, so it is
    /// exactly as unregistrable as one carrying none. Counting it as identified would leave a figure
    /// the reader could not reconcile with the two counts beside it.
    /// </remarks>
    [Fact]
    public async Task ASceneIdentifiedOnlyInTheOtherNamespaceIsCountedAsUnidentified()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), OtherNamespaceEndpoint, FirstScene);

        Assert.Equal(1, await UnidentifiedAsync(host));
    }

    /// <summary>
    /// A scene carrying two spellings of one source is counted as identified once.
    /// </summary>
    /// <remarks>
    /// The pair is two rows in the database and one identified scene under the host's own rule, so a
    /// count that read the rows would report fewer unidentified scenes than there are.
    /// </remarks>
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

    /// <summary>
    /// A library where nothing is identified answers no identifier and counts every scene.
    /// </summary>
    /// <remarks>
    /// The pairing is the claim: the count and the stream are two members and a reader adds their
    /// answers together, so a library at one extreme has to be reported consistently by both.
    /// </remarks>
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

    /// <summary>Two scenes under one entity are both answered.</summary>
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
