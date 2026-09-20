using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

// A library reaches millions of files, so a folder set assembled by loading every file and reducing
// it in memory would answer correctly and be unusable. That is why the shape of the read is
// asserted beside the answer.
public sealed class EntityFolderPortTests
{
    private const string Earlier = "/library/vixen/2025";
    private const string Later = "/library/vixen/2026";

    // A library root spelled the way the host stores one.
    private const string FirstRoot = "G:/Downloads/P";

    private const string SecondRoot = "I:/Downloads/P";

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

    // A performer's files reach it through the join row, a different table from the column a
    // studio's files carry.
    [Fact]
    public async Task APerformerLinkedToAVideoAnswersThatVideosFolder()
    {
        await using var host = await MonitorHost.CreateAsync();
        var performerId = await host.SeedPerformerAsync(null, null);
        await host.SeedPerformerFileAsync(performerId, Later);

        Assert.Equal([Later], await FoldersOf(host, WhisparrEntityKind.Performer, performerId));
    }

    // Both kinds are seeded in one library, so an arm reading the other kind's table finds rows
    // rather than nothing.
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

    // The request that reads a folder throws on a blank path rather than answering nothing. A real
    // folder is seeded beside the blank one, so excluding the blank is told apart from answering
    // nothing at all.
    [Fact]
    public async Task AFileWhoseParentFolderPathIsBlankYieldsNoFolder()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, string.Empty);
        await host.SeedStudioFileAsync(studioId, Later);

        Assert.Equal([Later], await FoldersOf(host, WhisparrEntityKind.Studio, studioId));
    }

    // Spaces rather than a tab: the two engines this runs against trim different sets, and a space
    // is in both. The case pins the predicate to whitespace emptiness.
    [Fact]
    public async Task AFileWhoseParentFolderPathIsOnlySpacesYieldsNoFolder()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, "   ");
        await host.SeedStudioFileAsync(studioId, Later);

        Assert.Equal([Later], await FoldersOf(host, WhisparrEntityKind.Studio, studioId));
    }

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

    [Fact]
    public async Task AnIdBelowOneAnswersNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioFileAsync(await host.SeedStudioAsync(null, null), Later);

        Assert.Empty(await FoldersOf(host, WhisparrEntityKind.Studio, 0));
        Assert.Empty(await FoldersOf(host, WhisparrEntityKind.Performer, -1));
    }

    [Fact]
    public async Task AKindThisProductDoesNotExpressIsAFaultRatherThanAnEmptyAnswer()
    {
        await using var host = await MonitorHost.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => FoldersOf(host, (WhisparrEntityKind)(-1), 1));
    }

    // Read off the source, because no behavioural assertion tells a query that de-duplicates from a
    // method that loads every row and reduces it. Both answer the same folders.
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

    [Fact]
    public async Task AStudioSplitAcrossTwoRootsAnswersEachRootsOwnCount()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, FirstRoot + "/Vixen/2025");
        await host.SeedStudioFileAsync(studioId, FirstRoot + "/Vixen/2026");
        await host.SeedStudioFileAsync(studioId, SecondRoot + "/Vixen/2026");

        Assert.Equal(2, await CountUnder(host, WhisparrEntityKind.Studio, studioId, FirstRoot));
        Assert.Equal(1, await CountUnder(host, WhisparrEntityKind.Studio, studioId, SecondRoot));
    }

    // The two roots share a name prefix on purpose: a count taken without the root's trailing
    // separator answers three for the shorter root.
    [Fact]
    public async Task ASiblingSharingTheRootsNamePrefixCountsUnderNeitherRoot()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, "/library/vixen/2026");
        await host.SeedStudioFileAsync(studioId, "/library/vixen-classics/2026");
        await host.SeedStudioFileAsync(studioId, "/library/vixen-classics/2025");

        Assert.Equal(
            1, await CountUnder(host, WhisparrEntityKind.Studio, studioId, "/library/vixen"));
        Assert.Equal(
            2,
            await CountUnder(host, WhisparrEntityKind.Studio, studioId, "/library/vixen-classics"));
    }

    // The library stores the forward-slash form, and a host configured on Windows names its roots
    // the other way, so a count comparing the two as typed would answer zero for every studio.
    [Fact]
    public async Task ARootSpelledWithBackslashesStillCountsItsFiles()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, FirstRoot + "/Vixen/2026");

        Assert.Equal(
            1,
            await CountUnder(host, WhisparrEntityKind.Studio, studioId, @"G:\Downloads\P"));
    }

    // Both kinds are seeded in one library under one root, so an arm reading the other kind's table
    // finds rows rather than nothing.
    [Fact]
    public async Task NeitherKindCountsTheOthersFiles()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var performerId = await host.SeedPerformerAsync(null, null);
        await host.SeedStudioFileAsync(studioId, FirstRoot + "/Vixen/2026");
        await host.SeedPerformerFileAsync(performerId, FirstRoot + "/Vixen/2025");
        await host.SeedPerformerFileAsync(performerId, FirstRoot + "/Vixen/2026");

        Assert.Equal(1, await CountUnder(host, WhisparrEntityKind.Studio, studioId, FirstRoot));
        Assert.Equal(
            2, await CountUnder(host, WhisparrEntityKind.Performer, performerId, FirstRoot));
    }

    [Fact]
    public async Task AnIdBelowOneCountsNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioFileAsync(
            await host.SeedStudioAsync(null, null), FirstRoot + "/Vixen/2026");

        Assert.Equal(0, await CountUnder(host, WhisparrEntityKind.Studio, 0, FirstRoot));
        Assert.Equal(0, await CountUnder(host, WhisparrEntityKind.Performer, -1, FirstRoot));
    }

    [Fact]
    public async Task AKindThisProductDoesNotExpressFaultsTheCount()
    {
        await using var host = await MonitorHost.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CountUnder(host, (WhisparrEntityKind)(-1), 1, FirstRoot));
    }

    // A blank root, if counted, would match every file in the library.
    [Fact]
    public async Task ABlankRootIsRefused()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => CountUnder(host, WhisparrEntityKind.Studio, studioId, "   "));
    }

    // Read off the source for the same reason as the folder read: a count assembled in memory
    // answers the same number. The narrowing is on the denormalized path column, so no folder row
    // is loaded.
    [Fact]
    public void ThePerRootCountIsTakenAsACount()
    {
        var source = PortSource();

        Assert.Contains("CountAsync(ct)", source, StringComparison.Ordinal);
        Assert.Contains("file.Path.StartsWith(prefix)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Select(file => file.Path)", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVideoSplitAcrossTwoRootsAnswersEachRootsOwnCount()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var videoId = await host.SeedVideoWithFilesAsync(
            studioId, FirstRoot + "/Vixen/2025", FirstRoot + "/Vixen/2026", SecondRoot + "/Vixen");

        Assert.Equal(2, await CountVideoUnder(host, videoId, FirstRoot));
        Assert.Equal(1, await CountVideoUnder(host, videoId, SecondRoot));
    }

    /// <summary>
    /// One video's count holds none of another video's files, even under the same studio and root.
    /// </summary>
    /// <remarks>
    /// The two videos share a studio on purpose: a count narrowed by the studio rather than by the
    /// video answers both videos' files and would send a scene to the root holding its studio's
    /// other files rather than its own.
    /// </remarks>
    [Fact]
    public async Task OneVideosCountHoldsNoneOfAnothersFiles()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var videoId = await host.SeedVideoWithFilesAsync(studioId, SecondRoot + "/Vixen");
        await host.SeedVideoWithFilesAsync(
            studioId, FirstRoot + "/Vixen/2025", FirstRoot + "/Vixen/2026");

        Assert.Equal(0, await CountVideoUnder(host, videoId, FirstRoot));
        Assert.Equal(1, await CountVideoUnder(host, videoId, SecondRoot));
    }

    /// <summary>A video id below one counts nothing rather than every file in the library.</summary>
    [Fact]
    public async Task AVideoIdBelowOneCountsNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedVideoWithFilesAsync(
            await host.SeedStudioAsync(null, null), FirstRoot + "/Vixen/2026");

        Assert.Equal(0, await CountVideoUnder(host, 0, FirstRoot));
        Assert.Equal(0, await CountVideoUnder(host, -1, FirstRoot));
    }

    /// <summary>A blank root is refused for a video too.</summary>
    [Fact]
    public async Task ABlankRootIsRefusedForAVideo()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedVideoWithFilesAsync(
            await host.SeedStudioAsync(null, null), FirstRoot + "/Vixen/2026");

        await Assert.ThrowsAsync<ArgumentException>(() => CountVideoUnder(host, videoId, "   "));
    }

    private static Task<int> CountUnder(
        MonitorHost host, WhisparrEntityKind kind, int coveId, string coveRoot)
        => host.Folders.FilesUnderAsync(kind, coveId, coveRoot, TestCt);

    private static Task<int> CountVideoUnder(MonitorHost host, int videoId, string coveRoot)
        => host.Folders.VideoFilesUnderAsync(videoId, coveRoot, TestCt);

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
