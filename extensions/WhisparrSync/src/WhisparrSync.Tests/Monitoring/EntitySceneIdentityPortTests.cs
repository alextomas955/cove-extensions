using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Which identifiers one entity's own scenes carry, read from a real relational library.
/// </summary>
/// <remarks>
/// The streaming shape is as much the subject as the identifiers are. A library reaches millions of
/// files, so a set assembled by loading every row and reducing it in memory would answer correctly
/// and be unusable, which is why the shape of the read is asserted beside the answer.
/// <para>
/// The namespace rule is the other half. A video carrying a link only in the other generation's
/// namespace is not an identified scene at all here, and a read comparing endpoint spellings as
/// strings would answer that an identified video carries no identity.
/// </para>
/// </remarks>
public sealed class EntitySceneIdentityPortTests
{
    /// <summary>The standard spelling of the source the newer generation identifies against.</summary>
    /// <remarks>
    /// A different spelling from the one <see cref="MonitorHost.StoredEndpoint"/> stores, and
    /// deliberately: the two name one source under the host's own rule.
    /// </remarks>
    private const string StandardStashDbAddress = "https://stashdb.org/graphql";

    /// <summary>A spelling belonging to the OTHER generation's namespace.</summary>
    private const string OtherNamespaceEndpoint = "theporndb.net/graphql";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// A studio's identified scenes are answered and its unidentified one is not.
    /// </summary>
    [Fact]
    public async Task AStudioAnswersOnlyTheScenesCarryingAnIdentifier()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, SecondScene);
        await host.SeedStudioSceneAsync(studioId, null, null);

        Assert.Equal(
            [FirstScene, SecondScene],
            await IdentitiesOf(host, WhisparrEntityKind.Studio, studioId));
    }

    /// <summary>
    /// A row written under a different spelling of the same source IS answered.
    /// </summary>
    /// <remarks>
    /// The host's own same-source rule decides it. Comparing the two as strings would answer that an
    /// identified video carries no identity, and its scene would then be offered to nothing.
    /// </remarks>
    [Fact]
    public async Task ASpellingOfTheSameSourceIsAnsweredRatherThanComparedAsAString()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, StandardStashDbAddress, FirstScene);

        Assert.Equal([FirstScene], await IdentitiesOf(host, WhisparrEntityKind.Studio, studioId));
    }

    /// <summary>
    /// A row in the other generation's namespace names nothing the connected instance could take.
    /// </summary>
    [Fact]
    public async Task ARowInTheOtherGenerationsNamespaceIsNotAnswered()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, OtherNamespaceEndpoint, FirstScene);

        Assert.Empty(await IdentitiesOf(host, WhisparrEntityKind.Studio, studioId));
    }

    /// <summary>
    /// A blank identifier is not an identifier, so nothing is offered for the scene carrying it.
    /// </summary>
    [Fact]
    public async Task ABlankOrWhitespaceIdentifierIsNotAnswered()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, string.Empty);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, "   ");

        Assert.Empty(await IdentitiesOf(host, WhisparrEntityKind.Studio, studioId));
    }

    /// <summary>
    /// A performer's scenes reach it through the join row, which is a different table from the
    /// column a studio's scenes carry.
    /// </summary>
    [Fact]
    public async Task APerformersScenesAreAnsweredThroughTheJoinRow()
    {
        await using var host = await MonitorHost.CreateAsync();
        var performerId = await host.SeedPerformerAsync(null, null);
        await host.SeedPerformerSceneAsync(performerId, MonitorHost.StoredEndpoint, FirstScene);

        Assert.Equal(
            [FirstScene], await IdentitiesOf(host, WhisparrEntityKind.Performer, performerId));
    }

    /// <summary>
    /// One entity's scenes are not another's, in both directions.
    /// </summary>
    [Fact]
    public async Task NeitherKindAnswersTheOthersScenes()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var performerId = await host.SeedPerformerAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedPerformerSceneAsync(performerId, MonitorHost.StoredEndpoint, SecondScene);

        Assert.Equal([FirstScene], await IdentitiesOf(host, WhisparrEntityKind.Studio, studioId));
        Assert.Equal(
            [SecondScene], await IdentitiesOf(host, WhisparrEntityKind.Performer, performerId));
    }

    [Fact]
    public async Task AnEntityHoldingNoScenesAnswersNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);

        Assert.Empty(await IdentitiesOf(host, WhisparrEntityKind.Studio, studioId));
    }

    /// <summary>
    /// An id below one answers nothing rather than every scene carrying no entity.
    /// </summary>
    [Fact]
    public async Task AnIdBelowOneAnswersNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);

        Assert.Empty(await IdentitiesOf(host, WhisparrEntityKind.Studio, 0));
        Assert.Empty(await IdentitiesOf(host, WhisparrEntityKind.Performer, -1));
    }

    /// <summary>
    /// A kind this product does not express is a fault, matching how the sibling sources treat it.
    /// </summary>
    [Fact]
    public async Task AKindThisProductDoesNotExpressIsAFaultRatherThanAnEmptyAnswer()
    {
        await using var host = await MonitorHost.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => IdentitiesOf(host, (WhisparrEntityKind)(-1), 1));
    }

    /// <summary>
    /// The answer is streamed and the de-duplication is the database's.
    /// </summary>
    /// <remarks>
    /// Read off the source, because no behavioural assertion can tell a query that de-duplicates
    /// from a method that loads every row and reduces it: both answer the same identifiers, and only
    /// one of them still works on a library of millions.
    /// </remarks>
    [Fact]
    public void TheSceneIdentityReadHoldsNothingPerScene()
    {
        var source = PortSource();

        Assert.Contains("Distinct()", source, StringComparison.Ordinal);
        Assert.Contains("IAsyncEnumerable<string>", source, StringComparison.Ordinal);
        Assert.All(
            new[] { "HashSet", "ToList", "ToArray", ".Take(" },
            accumulating => Assert.DoesNotContain(accumulating, source, StringComparison.Ordinal));
    }

    private static async Task<List<string>> IdentitiesOf(
        MonitorHost host, WhisparrEntityKind kind, int coveId)
    {
        var identities = new List<string>();
        await foreach (var identity in host.SceneIdentities.SceneIdentitiesFor(
            kind, coveId, WhisparrGeneration.V3, TestCt))
        {
            identities.Add(identity);
        }

        return identities;
    }

    // Found by walking up to the extension directory rather than by a counted-out "..": the test
    // assembly's depth below it varies with configuration and target framework.
    private static string PortSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "WhisparrSync", "Monitoring", "EntitySceneIdentityPort.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new InvalidOperationException(
            $"No src/WhisparrSync/Monitoring/EntitySceneIdentityPort.cs above {AppContext.BaseDirectory}.");
    }
}
