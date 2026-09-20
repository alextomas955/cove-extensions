using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Addressing;

// One row whatever the root holds. A library reaches millions of files, so a read that materialised
// a root's files to pick one would answer correctly and be unusable.
public sealed class SampleFilePortTests
{
    private const string Root = "/library/vixen";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARootTheLibraryHoldsFilesUnderAnswersOneOfThemWithItsSize()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var seeded = await host.SeedStudioFileAsync(studioId, Root + "/2026", 41);

        var sample = await host.SampleFiles.ReadSampleFileAsync(Root, TestCt);

        Assert.NotNull(sample);
        Assert.Equal(seeded, sample.Path);
        Assert.Equal(41, sample.Size);
    }

    [Fact]
    public async Task ARootTheLibraryHoldsNoFileUnderAnswersNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, "/elsewhere/2026", 41);

        Assert.Null(await host.SampleFiles.ReadSampleFileAsync(Root, TestCt));
    }

    [Theory]
    [InlineData("/library/vixen")]
    [InlineData("\\library\\vixen")]
    [InlineData("/library/vixen/")]
    public async Task EitherSpellingOfTheRootAnswersTheSameFile(string spelling)
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var seeded = await host.SeedStudioFileAsync(studioId, Root + "/2026", 41);

        var sample = await host.SampleFiles.ReadSampleFileAsync(spelling, TestCt);

        Assert.NotNull(sample);
        Assert.Equal(seeded, sample.Path);
    }

    // The probes a run takes are repeatable only if the same root answers the same file.
    [Fact]
    public async Task ARootHoldingSeveralFilesAnswersTheSameOneEveryTime()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, Root + "/2026", 41);
        await host.SeedStudioFileAsync(studioId, Root + "/2025", 42);

        var first = await host.SampleFiles.ReadSampleFileAsync(Root, TestCt);
        var second = await host.SampleFiles.ReadSampleFileAsync(Root, TestCt);

        Assert.NotNull(first);
        Assert.Equal(first.Path, second?.Path);
    }

    // The prefix carries the separator, so a sibling whose name begins with the root's own does not
    // answer for it.
    [Fact]
    public async Task ASiblingWhoseNameStartsWithTheRootsIsNotUnderIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(studioId, Root + "22/2026", 41);

        Assert.Null(await host.SampleFiles.ReadSampleFileAsync(Root, TestCt));
    }

    [Fact]
    public async Task ABlankRootIsRefused()
    {
        await using var host = await MonitorHost.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => host.SampleFiles.ReadSampleFileAsync("  ", TestCt));
    }
}
