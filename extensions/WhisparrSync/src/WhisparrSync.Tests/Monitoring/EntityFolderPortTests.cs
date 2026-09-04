using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Which folders one entity's own files sit in, read from a real relational library.
/// </summary>
/// <remarks>
/// The de-duplication is as much the subject as the paths are. A library reaches millions of files,
/// so a folder set assembled by loading every file and reducing it in memory would answer correctly
/// and be unusable, which is why the shape of the read is asserted beside the answer.
/// </remarks>
public sealed class EntityFolderPortTests
{
    private const string Earlier = "/library/vixen/2025";
    private const string Later = "/library/vixen/2026";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AStudioWithTwoFilesInOneFolderAndOneInAnotherAnswersEachFolderOnce()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, Later);
        await host.SeedStudioFileAsync(studioId, Later);
        await host.SeedStudioFileAsync(studioId, Earlier);

        Assert.Equal([Earlier, Later], await FoldersOf(host, WhisparrEntityKind.Studio, studioId));
    }

    /// <summary>
    /// A performer's files reach it through the join row, which is a different table from the column
    /// a studio's files carry.
    /// </summary>
    [Fact]
    public async Task APerformerLinkedToAVideoAnswersThatVideosFolder()
    {
        await using var host = await MonitorHost.CreateAsync();
        var performerId = await host.SeedPerformerAsync(null, null);
        await host.SeedPerformerFileAsync(performerId, Later);

        Assert.Equal([Later], await FoldersOf(host, WhisparrEntityKind.Performer, performerId));
    }

    /// <summary>
    /// One entity's files are not another's, in both directions.
    /// </summary>
    /// <remarks>
    /// Both kinds are seeded in one library, so an arm reading the other kind's table finds rows
    /// rather than nothing and the mistake is visible.
    /// </remarks>
    [Fact]
    public async Task NeitherKindAnswersTheOthersFolders()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var performerId = await host.SeedPerformerAsync(null, null);
        await host.SeedStudioFileAsync(studioId, Later);
        await host.SeedPerformerFileAsync(performerId, Earlier);

        Assert.Equal([Later], await FoldersOf(host, WhisparrEntityKind.Studio, studioId));
        Assert.Equal([Earlier], await FoldersOf(host, WhisparrEntityKind.Performer, performerId));
    }

    /// <summary>
    /// A folder the library holds under a blank path is not offered, because the request that reads
    /// a folder refuses a blank one by throwing rather than by answering nothing.
    /// </summary>
    /// <remarks>
    /// The real one is seeded beside it so the case separates "excluded the blank" from "answered
    /// nothing at all".
    /// </remarks>
    [Fact]
    public async Task AFileWhoseParentFolderPathIsBlankYieldsNoFolder()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, string.Empty);
        await host.SeedStudioFileAsync(studioId, Later);

        Assert.Equal([Later], await FoldersOf(host, WhisparrEntityKind.Studio, studioId));
    }

    /// <summary>
    /// A path of nothing but spaces is excluded too, matching the consumer's guard rather than a
    /// narrower emptiness test.
    /// </summary>
    /// <remarks>
    /// Spaces rather than a tab: the two engines this runs against trim different sets, and a space
    /// is in both. This case is what pins the predicate to whitespace emptiness, so narrowing it to
    /// an empty-string comparison later goes red here.
    /// </remarks>
    [Fact]
    public async Task AFileWhoseParentFolderPathIsOnlySpacesYieldsNoFolder()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, "   ");
        await host.SeedStudioFileAsync(studioId, Later);

        Assert.Equal([Later], await FoldersOf(host, WhisparrEntityKind.Studio, studioId));
    }

    /// <summary>
    /// Two files in one folder are one folder to read, whatever else the entity holds.
    /// </summary>
    [Fact]
    public async Task TwoFilesInOneFolderYieldOneFolder()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, Earlier);
        await host.SeedStudioFileAsync(studioId, Earlier);

        Assert.Equal([Earlier], await FoldersOf(host, WhisparrEntityKind.Studio, studioId));
    }

    [Fact]
    public async Task AnEntityHoldingNoFilesAnswersNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);

        Assert.Empty(await FoldersOf(host, WhisparrEntityKind.Studio, studioId));
    }

    /// <summary>
    /// An id below one answers nothing rather than every file carrying no entity.
    /// </summary>
    [Fact]
    public async Task AnIdBelowOneAnswersNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioFileAsync(await host.SeedStudioAsync(null, null), Later);

        Assert.Empty(await FoldersOf(host, WhisparrEntityKind.Studio, 0));
        Assert.Empty(await FoldersOf(host, WhisparrEntityKind.Performer, -1));
    }

    /// <summary>
    /// A kind this product does not express is a fault, matching how identity treats the same case.
    /// </summary>
    [Fact]
    public async Task AKindThisProductDoesNotExpressIsAFaultRatherThanAnEmptyAnswer()
    {
        await using var host = await MonitorHost.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => FoldersOf(host, (WhisparrEntityKind)(-1), 1));
    }

    /// <summary>
    /// The answer is streamed and the de-duplication is the database's.
    /// </summary>
    /// <remarks>
    /// Read off the source, because no behavioural assertion can tell a query that de-duplicates from
    /// a method that loads every row and reduces it: both answer the same folders, and only one of
    /// them still works on a library of millions.
    /// </remarks>
    [Fact]
    public void TheFolderReadHoldsNothingPerFile()
    {
        var source = PortSource();

        Assert.Contains("Distinct()", source, StringComparison.Ordinal);
        Assert.Contains("IAsyncEnumerable<string>", source, StringComparison.Ordinal);
        Assert.All(
            new[] { "HashSet", "ToList", "ToArray", ".Take(" },
            accumulating => Assert.DoesNotContain(accumulating, source, StringComparison.Ordinal));
    }

    private static async Task<List<string>> FoldersOf(
        MonitorHost host, WhisparrEntityKind kind, int coveId)
    {
        var folders = new List<string>();
        await foreach (var folder in host.Folders.FoldersFor(kind, coveId, TestCt))
        {
            folders.Add(folder);
        }

        return folders;
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
                directory.FullName, "src", "WhisparrSync", "Monitoring", "EntityFolderPort.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new InvalidOperationException(
            $"No src/WhisparrSync/Monitoring/EntityFolderPort.cs above {AppContext.BaseDirectory}.");
    }
}
