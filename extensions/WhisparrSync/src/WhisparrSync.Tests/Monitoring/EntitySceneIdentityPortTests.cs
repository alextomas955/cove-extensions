using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

// A library reaches millions of files, so a set assembled by loading every row and reducing it in
// memory answers correctly and is unusable. The shape of the read is asserted beside the answer
// for that reason.
public sealed class EntitySceneIdentityPortTests
{
    // The standard spelling of the source v3 identifies against. It differs from the spelling
    // MonitorHost.StoredEndpoint stores, and the host's rule treats the two as one source.
    private const string StandardStashDbAddress = "https://stashdb.org/graphql";

    // A spelling belonging to v2's namespace.
    private const string OtherNamespaceEndpoint = "theporndb.net/graphql";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

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

    // The host's same-source rule decides this. Comparing endpoint spellings as strings would
    // answer that an identified video carries no identity, and its scene would be offered to
    // nothing.
    [Fact]
    public async Task ASpellingOfTheSameSourceIsAnsweredRatherThanComparedAsAString()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, StandardStashDbAddress, FirstScene);

        Assert.Equal([FirstScene], await IdentitiesOf(host, WhisparrEntityKind.Studio, studioId));
    }

    // A row in v2's namespace names nothing a connected v3 instance could take.
    [Fact]
    public async Task ARowInTheOtherGenerationsNamespaceIsNotAnswered()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, OtherNamespaceEndpoint, FirstScene);

        Assert.Empty(await IdentitiesOf(host, WhisparrEntityKind.Studio, studioId));
    }

    [Fact]
    public async Task ABlankOrWhitespaceIdentifierIsNotAnswered()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, string.Empty);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, "   ");

        Assert.Empty(await IdentitiesOf(host, WhisparrEntityKind.Studio, studioId));
    }

    // A performer's scenes reach it through a join table, not through the column a studio's scenes
    // carry.
    [Fact]
    public async Task APerformersScenesAreAnsweredThroughTheJoinRow()
    {
        await using var host = await MonitorHost.CreateAsync();
        var performerId = await host.SeedPerformerAsync(null, null);
        await host.SeedPerformerSceneAsync(performerId, MonitorHost.StoredEndpoint, FirstScene);

        Assert.Equal(
            [FirstScene], await IdentitiesOf(host, WhisparrEntityKind.Performer, performerId));
    }

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

    // An id below one answers nothing rather than every scene carrying no entity.
    [Fact]
    public async Task AnIdBelowOneAnswersNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);

        Assert.Empty(await IdentitiesOf(host, WhisparrEntityKind.Studio, 0));
        Assert.Empty(await IdentitiesOf(host, WhisparrEntityKind.Performer, -1));
    }

    [Fact]
    public async Task AKindThisProductDoesNotExpressIsAFaultRatherThanAnEmptyAnswer()
    {
        await using var host = await MonitorHost.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => IdentitiesOf(host, (WhisparrEntityKind)(-1), 1));
    }

    // Read off the source, because no behavioural assertion can tell a query that de-duplicates in
    // the database from one that loads every row and reduces it. Both answer the same identifiers.
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
